using NexusFlow.Core.Diagnostics;
using NexusFlow.Core.Discovery;
using NexusFlow.Core.InputTransport;
using NexusFlow.Core.Routing;
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
	private readonly IDiagnosticsLog _log;

	// Optional: only if you already have these registered
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

	// NOTE: sender/peers are optional so Phase F.1 can work without wiring transport yet.
	public LocalInputCaptureOrchestrator(
		ILocalIdentity identity,
		IWinHookCaptureService capture,
		RoutingEngine routing,
		IDiagnosticsLog log,
		InputSender? sender = null,
		IPeerEndpointResolver? peers = null)
	{
		_identity = identity;
		_capture = capture;
		_routing = routing;
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

	private void OnKey(CapturedKeyEvent e) => OnActivity(CapturedInputKind.Key, e.TimestampUtcTicks);
	private void OnMove(CapturedMouseMoveEvent e) => OnActivity(CapturedInputKind.MouseMove, e.TimestampUtcTicks);
	private void OnButton(CapturedMouseButtonEvent e) => OnActivity(CapturedInputKind.MouseButton, e.TimestampUtcTicks);
	private void OnWheel(CapturedMouseWheelEvent e) => OnActivity(CapturedInputKind.MouseWheel, e.TimestampUtcTicks);

	// IMPORTANT: must be fast, non-blocking (runs on hook thread)
	private void OnActivity(CapturedInputKind kind, long ticks)
	{
		// Throttle hot path: mouse move is extremely frequent.
		if (kind == CapturedInputKind.MouseMove)
		{
			var last = Volatile.Read(ref _lastMouseMoveLogTicks);
			if (ticks - last < MouseMoveLogIntervalTicks)
				return;

			Volatile.Write(ref _lastMouseMoveLogTicks, ticks);
		}

		// Phase F.1: local-only flip to self (no broadcast)
		if (_routing.ActiveSourcePeerId != _identity.PeerId)
			_ = _routing.SetActiveSourceLocalOnlyAsync(_identity.PeerId);

		_log.Trace(Cat, $"Local input: {kind}");

		// Phase F.2 prep: enqueue outbound input (no await on hook thread)
		// If transport not wired yet, this is harmless.
		var ev = BuildInputEvent(kind, ticks);
		_out.Writer.TryWrite(ev);
	}

	private InputEventV1 BuildInputEvent(CapturedInputKind kind, long ticks)
	{
		var seq = Interlocked.Increment(ref _localSeq);

		var mappedKind = kind switch
		{
			CapturedInputKind.Key => InputKind.Key,
			CapturedInputKind.MouseMove => InputKind.MouseMove,
			CapturedInputKind.MouseButton => InputKind.MouseButton,
			CapturedInputKind.MouseWheel => InputKind.MouseWheel,
			_ => InputKind.Key
		};

		return new InputEventV1(
			FromPeerId: _identity.PeerId,
			Seq: seq,
			TimestampUtcTicks: ticks,
			Kind: mappedKind,
			Key: null,
			Move: null,
			Button: null,
			Wheel: null
		);
	}

	private async Task SenderLoopAsync()
	{
		try
		{
			while (await _out.Reader.WaitToReadAsync(_cts.Token).ConfigureAwait(false))
			{
				while (_out.Reader.TryRead(out var ev))
				{
					await TrySendAsync(ev, _cts.Token).ConfigureAwait(false);
				}
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
		// Transport not yet wired -> no-op.
		if (_sender is null || _peers is null || _routing.isFailsafeActive) return;

		// Only send if ActiveTarget != self
		var targetPeerId = _routing.ActiveTargetPeerId;
		if (string.Equals(targetPeerId, _identity.PeerId, StringComparison.Ordinal))
			return;
		try
		{
			if (_peers.TryGetEndpoint(targetPeerId, out var host, out var port))
			{
				await _sender.EnsureConnectedAsync(host, TcpPort, ct);
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
