using NexusFlow.Core.Diagnostics;
using NexusFlow.Core.Routing;
using NexusFlow.Identity;      // <-- adjust namespace
using NexusFlow.Input;

namespace NexusFlow.Core.Input;

public sealed class LocalInputCaptureOrchestrator : IDisposable
{
	private const string Cat = "input";

	private readonly ILocalIdentity _identity;
	private readonly IWinHookCaptureService _capture;
	private readonly RoutingEngine _routing;
	private readonly IDiagnosticsLog _log;

	public LocalInputCaptureOrchestrator(
		ILocalIdentity identity,
		IWinHookCaptureService capture,
		RoutingEngine routing,
		IDiagnosticsLog log)
	{
		_identity = identity;
		_capture = capture;
		_routing = routing;
		_log = log;

		_capture.Key += OnKey;
		_capture.MouseMove += OnMove;
		_capture.MouseButton += OnButton;
		_capture.MouseWheel += OnWheel;
	}

	public void Start() => _capture.Start();
	public void Stop() => _capture.Stop();

	private void OnKey(CapturedKeyEvent e) => OnActivity(CapturedInputKind.Key, e.TimestampUtcTicks);
	private void OnMove(CapturedMouseMoveEvent e) => OnActivity(CapturedInputKind.MouseMove, e.TimestampUtcTicks);
	private void OnButton(CapturedMouseButtonEvent e) => OnActivity(CapturedInputKind.MouseButton, e.TimestampUtcTicks);
	private void OnWheel(CapturedMouseWheelEvent e) => OnActivity(CapturedInputKind.MouseWheel, e.TimestampUtcTicks);

	private void OnActivity(CapturedInputKind kind, long ticks)
	{
		var localPeerId = _identity.PeerId; // <-- property name may differ

		// Phase F.1: local-only (no broadcast)
		_ = _routing.SetActiveSourceLocalOnlyAsync(localPeerId);

		_log.Trace(Cat, $"Local input: {kind} @ {ticks}");
	}

	public void Dispose()
	{
		_capture.Key -= OnKey;
		_capture.MouseMove -= OnMove;
		_capture.MouseButton -= OnButton;
		_capture.MouseWheel -= OnWheel;
		_capture.Stop();
	}
}
