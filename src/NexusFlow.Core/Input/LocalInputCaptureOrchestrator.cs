using NexusFlow.Core.Diagnostics;
using NexusFlow.Core.Discovery;
using NexusFlow.Core.InputTransport;
using NexusFlow.Core.Routing;
using NexusFlow.Core.Services;
using NexusFlow.Identity;
using NexusFlow.Input;
using NexusFlow.Protocol.Input;
using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace NexusFlow.Core.Input;

public sealed class LocalInputCaptureOrchestrator : IDisposable
{
	private const string Cat = "input";
	private const int TcpPort = 49800;

	private readonly ILocalIdentity _identity;
	private readonly IWinHookCaptureService _capture;
	private readonly RoutingEngine _routing;
	private readonly IFailsafeService _failsafe;
	private readonly IDiagnosticsLog _log;

	private readonly InputSender? _sender;
	private readonly IPeerEndpointResolver? _peers;

	private long _lastMouseMoveLogTicks;
	private static readonly long MouseMoveLogIntervalTicks = TimeSpan.FromMilliseconds(75).Ticks;

	private long _localSeq;

	private readonly Channel<InputEventV1> _out =
		Channel.CreateBounded<InputEventV1>(
			new BoundedChannelOptions(4096)
			{
				SingleReader = true,
				SingleWriter = false,
				FullMode = BoundedChannelFullMode.DropOldest
			});

	private readonly CancellationTokenSource _cts = new();
	private readonly Task _senderLoop;

	public LocalInputCaptureOrchestrator(
		ILocalIdentity identity,
		IWinHookCaptureService capture,
		RoutingEngine routing,
		IFailsafeService failsafe,
		IDiagnosticsLog log,
		InputSender? sender = null,
		IPeerEndpointResolver? peers = null)
	{
		_identity = identity;
		_capture = capture;
		_routing = routing;
		_failsafe = failsafe;
		_log = log;

		_sender = sender;
		_peers = peers;

		_capture.Key += OnKey;
		_capture.MouseMove += OnMove;
		_capture.MouseButton += OnButton;
		_capture.MouseWheel += OnWheel;

		_senderLoop = Task.Run(SenderLoopAsync);
	}

	public void Start() => _capture.Start();
	public void Stop() => _capture.Stop();

	// ---------- Hook thread handlers (no await, no blocking) ----------

	private void OnKey(CapturedKeyEvent e)
	{
		FlipLocalSourceIfNeeded();
		_out.Writer.TryWrite(BuildKeyEvent(e));
	}

	private void OnMove(CapturedMouseMoveEvent e)
	{
		_out.Writer.TryWrite(BuildMoveEvent(e));
		if (ThrottleMouse(e.TimestampUtcTicks)) return;
		FlipLocalSourceIfNeeded();	
	}

	private void OnButton(CapturedMouseButtonEvent e)
	{
		FlipLocalSourceIfNeeded();
		_out.Writer.TryWrite(BuildButtonEvent(e));
	}

	private void OnWheel(CapturedMouseWheelEvent e)
	{
		FlipLocalSourceIfNeeded();
		_out.Writer.TryWrite(BuildWheelEvent(e));
	}

	private void FlipLocalSourceIfNeeded()
	{
		// Phase F: local-only
		if (_routing.ActiveSourcePeerId != _identity.PeerId)
			_ = _routing.SetActiveSourceLocalOnlyAsync(_identity.PeerId);

		_log.Trace(Cat, "Local input");
	}

	private bool ThrottleMouse(long ticks)
	{
		var last = Volatile.Read(ref _lastMouseMoveLogTicks);
		if (ticks - last < MouseMoveLogIntervalTicks)
			return true;

		Volatile.Write(ref _lastMouseMoveLogTicks, ticks);
		return false;
	}

	// ---------- Protocol mapping (end-to-end payload structs) ----------

	private InputEventV1 BuildKeyEvent(CapturedKeyEvent e)
	{
		var seq = Interlocked.Increment(ref _localSeq);

		return new InputEventV1(
			FromPeerId: _identity.PeerId,
			Seq: seq,
			TimestampUtcTicks: e.TimestampUtcTicks,
			Kind: InputKind.Key,
			Key: new InputKeyPayload(
				VkCode: e.VkCode,
				ScanCode: e.ScanCode,
				IsDown: e.Action == CapturedKeyAction.Down
			),
			Move: null,
			Button: null,
			Wheel: null
		);
	}

	private InputEventV1 BuildMoveEvent(CapturedMouseMoveEvent e)
	{
		var seq = Interlocked.Increment(ref _localSeq);

		return new InputEventV1(
			FromPeerId: _identity.PeerId,
			Seq: seq,
			TimestampUtcTicks: e.TimestampUtcTicks,
			Kind: InputKind.MouseMove,
			Key: null,
			Move: new InputMouseMovePayload(
				Dx: e.Dx,
				Dy: e.Dy,
				X: e.X,
				Y: e.Y
			),
			Button: null,
			Wheel: null
		);
	}

	private InputEventV1 BuildButtonEvent(CapturedMouseButtonEvent e)
	{
		var seq = Interlocked.Increment(ref _localSeq);

		// Protocol uses byte (keep it stable for v1)
		// 1=Left, 2=Right, 3=Middle
		byte btn = e.Button switch
		{
			CapturedMouseButton.Left => 1,
			CapturedMouseButton.Right => 2,
			CapturedMouseButton.Middle => 3,
			_ => 1
		};

		return new InputEventV1(
			FromPeerId: _identity.PeerId,
			Seq: seq,
			TimestampUtcTicks: e.TimestampUtcTicks,
			Kind: InputKind.MouseButton,
			Key: null,
			Move: null,
			Button: new InputMouseButtonPayload(
				Button: btn,
				IsDown: e.Action == MouseButtonAction.Down,
				X: e.X,
				Y: e.Y
			),
			Wheel: null
		);
	}

	private InputEventV1 BuildWheelEvent(CapturedMouseWheelEvent e)
	{
		var seq = Interlocked.Increment(ref _localSeq);

		return new InputEventV1(
			FromPeerId: _identity.PeerId,
			Seq: seq,
			TimestampUtcTicks: e.TimestampUtcTicks,
			Kind: InputKind.MouseWheel,
			Key: null,
			Move: null,
			Button: null,
			Wheel: new InputMouseWheelPayload(
				Delta: e.Delta,
				X: e.X,
				Y: e.Y
			)
		);
	}

	// ---------- Sender loop (async, off hook thread) ----------

	private async Task SenderLoopAsync()
	{
		try
		{
			while (await _out.Reader.WaitToReadAsync(_cts.Token).ConfigureAwait(false))
			{
				while (_out.Reader.TryRead(out var ev))
					await TrySendAsync(ev, _cts.Token).ConfigureAwait(false);
			}
		}
		catch (OperationCanceledException) { }
		catch (Exception ex)
		{
			_log.Error(Cat, $"SenderLoop crashed: {ex.Message}");
		}
	}

	private async Task TrySendAsync(InputEventV1 ev, CancellationToken ct)
	{
		if (_sender is null || _peers is null) return;
		if (_failsafe.IsBlocked) return;

		var targetPeerId = _routing.ActiveTargetPeerId;
		if (string.Equals(targetPeerId, _identity.PeerId, StringComparison.Ordinal))
			return;

		try
		{
			if (_peers.TryGetEndpoint(targetPeerId, out var host, out var port))
			{
				await _sender.EnsureConnectedAsync(targetPeerId, host, port, ct).ConfigureAwait(false);
				await _sender.SendAsync(ev, ct).ConfigureAwait(false);
			}
		}
		catch (Exception ex)
		{
			_log.Warn(Cat, $"Input send failed to {targetPeerId}: {ex.Message}");
		}
	}

	public void Dispose()
	{
		try
		{
			_cts.Cancel();
			_out.Writer.TryComplete();
			try { _senderLoop.Wait(250); } catch { }

			_capture.Key -= OnKey;
			_capture.MouseMove -= OnMove;
			_capture.MouseButton -= OnButton;
			_capture.MouseWheel -= OnWheel;

			_capture.Stop();
		}
		catch { }
		finally
		{
			_cts.Dispose();
		}
	}
}
