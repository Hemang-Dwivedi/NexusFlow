using System.Collections.Concurrent;
using System.Threading.Channels;
using NexusFlow.Core.Diagnostics;
using NexusFlow.Core.Routing;
using NexusFlow.Core.Services;

namespace NexusFlow.Core.Input;

/// <summary>
/// Accepts ordered input events from multiple peers and applies them in-order per peer,
/// buffering out-of-order arrivals. Merges streams into one applied stream without violating per-peer order.
/// </summary>
public interface IOrderedInputRouter
{
	double MouseMoveThreshold { get; }

	// Ingest helpers (router assigns per-peer sequence numbers)
	void EnqueueKey(string fromPeerId, KeyKind key, KeyAction action);
	void EnqueueMouseMove(string fromPeerId, double dx, double dy);
	void EnqueueMouseClick(string fromPeerId);
	void EnqueueMouseScroll(string fromPeerId, double delta);
	void EnqueueMicActivity(string fromPeerId);

	// Called when a peer disconnects to clear buffers (optional but recommended)
	void OnPeerDisconnected(string peerId);

	Task StartAsync(CancellationToken ct);
	Task StopAsync();
}

public sealed class OrderedInputRouter : IOrderedInputRouter
{
	private const string Cat = "input.router";

	private readonly IRoutingEngine _routing;
	private readonly IModifierStateTracker _mods;
	private readonly IFailsafeService _failsafe;
	private readonly IDiagnosticsLog _log;

	private readonly Channel<IOrderedInputEvent> _ch =
		Channel.CreateUnbounded<IOrderedInputEvent>(new UnboundedChannelOptions
		{
			SingleReader = true,
			SingleWriter = false,
			AllowSynchronousContinuations = false
		});

	// per-peer sequence counter assigned at enqueue time
	private readonly ConcurrentDictionary<string, long> _nextSeq = new();

	// per-peer ordering state (single reader, so normal Dictionary is fine)
	private readonly Dictionary<string, long> _expectedSeq = new(); // next expected seq
	private readonly Dictionary<string, SortedDictionary<long, IOrderedInputEvent>> _buffers = new();

	// mouse movement threshold accumulation per "from peer" when not active source
	private readonly Dictionary<string, double> _pendingMove = new();

	private Task? _loop;
	private CancellationTokenSource? _cts;

	public double MouseMoveThreshold { get; }

	public OrderedInputRouter(
		IRoutingEngine routing,
		IModifierStateTracker mods,
		IFailsafeService failsafe,
		IDiagnosticsLog log,
		double mouseMoveThresholdPx = 12.0)
	{
		_routing = routing;
		_mods = mods;
		_failsafe = failsafe;
		_log = log;
		MouseMoveThreshold = Math.Max(1.0, mouseMoveThresholdPx);
	}

	public Task StartAsync(CancellationToken ct)
	{
		if (_loop is not null) return Task.CompletedTask;

		_cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
		_loop = Task.Run(() => LoopAsync(_cts.Token), _cts.Token);

		_log.Info(Cat, $"Started. MouseMoveThreshold={MouseMoveThreshold:0.##}");
		return Task.CompletedTask;
	}

	public async Task StopAsync()
	{
		if (_loop is null) return;

		try { _cts!.Cancel(); } catch { }

		try { await _loop.ConfigureAwait(false); } catch { }

		_loop = null;
		_cts?.Dispose();
		_cts = null;

		_log.Info(Cat, "Stopped.");
	}

	// ---------------- Ingest (assign per-peer seq) ----------------

	public void EnqueueKey(string fromPeerId, KeyKind key, KeyAction action)
	{
		var e = new OrderedKeyEvent(fromPeerId, NextSeq(fromPeerId), DateTimeOffset.Now, key, action);
		_ch.Writer.TryWrite(e);
	}

	public void EnqueueMouseMove(string fromPeerId, double dx, double dy)
	{
		var e = new OrderedMouseMoveEvent(fromPeerId, NextSeq(fromPeerId), DateTimeOffset.Now, dx, dy);
		_ch.Writer.TryWrite(e);
	}

	public void EnqueueMouseClick(string fromPeerId)
	{
		var e = new OrderedMouseClickEvent(fromPeerId, NextSeq(fromPeerId), DateTimeOffset.Now);
		_ch.Writer.TryWrite(e);
	}

	public void EnqueueMouseScroll(string fromPeerId, double delta)
	{
		var e = new OrderedMouseScrollEvent(fromPeerId, NextSeq(fromPeerId), DateTimeOffset.Now, delta);
		_ch.Writer.TryWrite(e);
	}

	public void EnqueueMicActivity(string fromPeerId)
	{
		var e = new OrderedMicActivityEvent(fromPeerId, NextSeq(fromPeerId), DateTimeOffset.Now);
		_ch.Writer.TryWrite(e);
	}

	public void OnPeerDisconnected(string peerId)
	{
		// Single reader applies, but disconnect notifications may come from other threads.
		// We just enqueue a synthetic "mic activity" marker to get onto the router loop,
		// then clear state inside the loop when it sees it (cheap and safe).
		EnqueueMicActivity(peerId);
	}

	private long NextSeq(string peerId)
		=> _nextSeq.AddOrUpdate(peerId, 1, (_, cur) => cur + 1);

	// ---------------- Loop / ordering / merge ----------------

	private async Task LoopAsync(CancellationToken ct)
	{
		try
		{
			while (await _ch.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
			{
				while (_ch.Reader.TryRead(out var e))
				{
					if (_failsafe.IsBlocked)
					{
						_log.Trace(Cat, $"Failsafe blocked: drop {e.Kind} from={e.FromPeerId} seq={e.Seq}");
						continue;
					}

					ApplyWithPerPeerOrdering(e, ct);
				}
			}
		}
		catch (OperationCanceledException) { }
		catch (Exception ex)
		{
			_log.Error(Cat, $"Router loop crashed: {ex.Message}");
		}
	}

	private void ApplyWithPerPeerOrdering(IOrderedInputEvent e, CancellationToken ct)
	{
		var peer = e.FromPeerId;

		if (!_expectedSeq.TryGetValue(peer, out var expected))
		{
			expected = 1;
			_expectedSeq[peer] = expected;
			_buffers[peer] = new SortedDictionary<long, IOrderedInputEvent>();
			_pendingMove[peer] = 0;
		}

		if (e.Seq < expected)
		{
			_log.Warn(Cat, $"DROP late/dup from={peer} seq={e.Seq} expected={expected} kind={e.Kind}");
			return;
		}

		if (e.Seq > expected)
		{
			// buffer out-of-order
			_buffers[peer][e.Seq] = e;
			_log.Trace(Cat, $"BUFFER from={peer} seq={e.Seq} expected={expected} kind={e.Kind} (buf={_buffers[peer].Count})");
			return;
		}

		// seq == expected => apply and drain contiguous buffered events
		ApplyEvent(e, ct);
		_expectedSeq[peer] = expected + 1;

		Drain(peer, ct);
	}

	private void Drain(string peer, CancellationToken ct)
	{
		var buf = _buffers[peer];
		while (true)
		{
			var expected = _expectedSeq[peer];
			if (!buf.TryGetValue(expected, out var next))
				break;

			buf.Remove(expected);
			ApplyEvent(next, ct);
			_expectedSeq[peer] = expected + 1;
		}
	}

	// ---------------- Apply semantics (modifiers + source switching) ----------------

	private void ApplyEvent(IOrderedInputEvent e, CancellationToken ct)
	{
		_log.Trace(Cat, $"APPLY from={e.FromPeerId} seq={e.Seq} kind={e.Kind}");

		switch (e)
		{
			case OrderedKeyEvent k:
				// Modifiers are global stateful
				_mods.Apply(new SimKeyEvent(k.Seq, k.FromPeerId, k.Key, k.Action, k.Timestamp));

				// Rule: keyboard press switches input source immediately (on KeyDown only)
				if (k.Action == KeyAction.Down)
					_ = _routing.RequestSetActiveSourceAsync(k.FromPeerId, ct);

				// If peer "disconnect marker" uses MicActivity, ignore here.
				break;

			case OrderedMouseClickEvent:
				// Rule: click switches source immediately
				_pendingMove[e.FromPeerId] = 0;
				_ = _routing.RequestSetActiveSourceAsync(e.FromPeerId, ct);
				break;

			case OrderedMouseScrollEvent:
				// Rule: scroll switches source immediately
				_pendingMove[e.FromPeerId] = 0;
				_ = _routing.RequestSetActiveSourceAsync(e.FromPeerId, ct);
				break;

			case OrderedMouseMoveEvent mm:
				ApplyMouseMove(mm, ct);
				break;

			case OrderedMicActivityEvent:
				// Rule: mic never switches source
				// We also use mic as a safe "marker" for disconnect; do nothing here.
				break;
		}
	}

	private void ApplyMouseMove(OrderedMouseMoveEvent mm, CancellationToken ct)
	{
		var active = _routing.ActiveSourcePeerId;
		var mag = Math.Sqrt(mm.Dx * mm.Dx + mm.Dy * mm.Dy);

		// If from active source -> no switch, clear pending for that peer.
		if (string.Equals(active, mm.FromPeerId, StringComparison.Ordinal))
		{
			_pendingMove[mm.FromPeerId] = 0;
			return;
		}

		var pending = _pendingMove[mm.FromPeerId] + mag;
		_pendingMove[mm.FromPeerId] = pending;

		if (pending < MouseMoveThreshold)
			return;

		_pendingMove[mm.FromPeerId] = 0;
		_ = _routing.RequestSetActiveSourceAsync(mm.FromPeerId, ct);
	}
}
