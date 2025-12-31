using NexusFlow.Core.Diagnostics;
using NexusFlow.Core.Routing;
using NexusFlow.Core.Services;

namespace NexusFlow.Core.Input;

public interface IInputSourceSwitchingSimulator
{
	string MovementThresholdInfo { get; }

	Task SimKeyPressAsync(string fromPeerId, CancellationToken ct = default);
	Task SimMouseClickAsync(string fromPeerId, CancellationToken ct = default);
	Task SimMouseScrollAsync(string fromPeerId, CancellationToken ct = default);
	Task SimMouseMoveAsync(string fromPeerId, double dx, double dy, CancellationToken ct = default);
	Task SimMicActivityAsync(string fromPeerId, CancellationToken ct = default);
}

public sealed class InputSourceSwitchingSimulator : IInputSourceSwitchingSimulator
{
	private const string Cat = "input.sim";
	private readonly IRoutingEngine _routing;
	private readonly IFailsafeService _failsafe;
	private readonly IDiagnosticsLog _log;

	// Accumulated mouse movement when the "from peer" is not the current active source.
	private double _pendingMove;

	public double Threshold { get; }

	public string MovementThresholdInfo => $"Mouse movement threshold = {Threshold:0.##} px (accumulated)";

	public InputSourceSwitchingSimulator(
		IRoutingEngine routing,
		IFailsafeService failsafe,
		IDiagnosticsLog log,
		double thresholdPx = 12.0)
	{
		_routing = routing;
		_failsafe = failsafe;
		_log = log;
		Threshold = Math.Max(1.0, thresholdPx);
	}

	public Task SimKeyPressAsync(string fromPeerId, CancellationToken ct = default)
		=> SwitchImmediateAsync(fromPeerId, "KeyPress", ct);

	public Task SimMouseClickAsync(string fromPeerId, CancellationToken ct = default)
		=> SwitchImmediateAsync(fromPeerId, "MouseClick", ct);

	public Task SimMouseScrollAsync(string fromPeerId, CancellationToken ct = default)
		=> SwitchImmediateAsync(fromPeerId, "MouseScroll", ct);

	public async Task SimMouseMoveAsync(string fromPeerId, double dx, double dy, CancellationToken ct = default)
	{
		if (_failsafe.IsBlocked)
		{
			_log.Warn(Cat, $"Failsafe blocked: ignoring MouseMove from={fromPeerId}");
			return;
		}

		var active = _routing.ActiveSourcePeerId;
		var magnitude = Math.Sqrt(dx * dx + dy * dy);

		// If movement is from the already-active source: reset pending and do nothing.
		if (string.Equals(active, fromPeerId, StringComparison.Ordinal))
		{
			_pendingMove = 0;
			_log.Trace(Cat, $"MouseMove from active source={fromPeerId} mag={magnitude:0.##} → no switch");
			return;
		}

		_pendingMove += magnitude;

		if (_pendingMove < Threshold)
		{
			_log.Trace(Cat, $"MouseMove from={fromPeerId} mag={magnitude:0.##}, pending={_pendingMove:0.##}/{Threshold:0.##} → no switch");
			return;
		}

		_pendingMove = 0;
		_log.Info(Cat, $"MouseMove exceeded threshold → switch ActiveSource -> {fromPeerId}");
		await _routing.RequestSetActiveSourceAsync(fromPeerId, ct).ConfigureAwait(false);
	}

	public Task SimMicActivityAsync(string fromPeerId, CancellationToken ct = default)
	{
		// Explicit rule: mic NEVER switches input source.
		_log.Trace(Cat, $"MicActivity from={fromPeerId} → never switches");
		return Task.CompletedTask;
	}

	private async Task SwitchImmediateAsync(string fromPeerId, string reason, CancellationToken ct)
	{
		if (_failsafe.IsBlocked)
		{
			_log.Warn(Cat, $"Failsafe blocked: ignoring {reason} from={fromPeerId}");
			return;
		}

		var active = _routing.ActiveSourcePeerId;
		if (string.Equals(active, fromPeerId, StringComparison.Ordinal))
		{
			_pendingMove = 0;
			_log.Trace(Cat, $"{reason} from active source={fromPeerId} → no switch");
			return;
		}

		_pendingMove = 0;
		_log.Info(Cat, $"{reason} → switch ActiveSource {active} -> {fromPeerId}");
		await _routing.RequestSetActiveSourceAsync(fromPeerId, ct).ConfigureAwait(false);
	}
}
