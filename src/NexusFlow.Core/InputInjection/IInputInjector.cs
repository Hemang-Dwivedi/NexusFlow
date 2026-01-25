using NexusFlow.Protocol.Input;

namespace NexusFlow.Core.InputInjection;

/// <summary>
/// Applies remote input events to the local machine.
/// MUST be fast, deterministic, and side-effect isolated.
/// </summary>
public interface IInputInjector
{
	void Inject(InputEventV1 ev);

	/// <summary>
	/// Safety reset: release all pressed keys/buttons.
	/// Called on failsafe, disconnect, or routing change.
	/// </summary>
	void Reset();
}
