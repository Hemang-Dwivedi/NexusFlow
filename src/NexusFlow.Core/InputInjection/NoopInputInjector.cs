using NexusFlow.Core.Diagnostics;
using NexusFlow.Protocol.Input;

namespace NexusFlow.Core.InputInjection;

/// <summary>
/// F.7 injector: does nothing except log.
/// Validates the entire receive → inject pipeline safely.
/// </summary>
public sealed class NoopInputInjector : IInputInjector
{
	private const string Cat = "inject-noop";
	private readonly IDiagnosticsLog _log;

	public NoopInputInjector(IDiagnosticsLog log)
	{
		_log = log;
	}

	public void Inject(InputEventV1 ev)
	{
		// Intentionally NO SIDE EFFECTS
		_log.Trace(
			Cat,
			$"NOOP inject: from={ev.FromPeerId} seq={ev.Seq} kind={ev.Kind}"
		);
	}

	public void Reset()
	{
		_log.Warn(Cat, "NOOP reset (no state)");
	}
}
