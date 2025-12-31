using NexusFlow.Core.Diagnostics;

namespace NexusFlow.Core.Input;

public interface IModifierStateTracker
{
	event Action<ModifierState>? Changed;

	ModifierState Current { get; }

	/// <summary>Apply an in-order key event. Modifier changes are updated and observable.</summary>
	void Apply(in SimKeyEvent e);

	/// <summary>Resets all modifier states (failsafe, disconnect, etc.).</summary>
	void Reset(string reason);
}

public sealed class ModifierStateTracker : IModifierStateTracker
{
	private const string Cat = "input.mods";

	private readonly object _gate = new();
	private readonly IDiagnosticsLog _log;

	private ModifierState _state;
	private long _lastSeq;

	public event Action<ModifierState>? Changed;

	public ModifierState Current
	{
		get { lock (_gate) return _state; }
	}

	public ModifierStateTracker(IDiagnosticsLog log)
	{
		_log = log;
		_state = new ModifierState(false, false, false, false);
		_lastSeq = 0;
	}

	public void Apply(in SimKeyEvent e)
	{
		ModifierState? newState = null;

		lock (_gate)
		{
			// Enforce strict in-order delivery in the simulator
			if (e.Seq <= _lastSeq)
			{
				_log.Warn(Cat, $"Out-of-order key event ignored: seq={e.Seq} last={_lastSeq} from={e.FromPeerId} {e.Key} {e.Action}");
				return;
			}

			_lastSeq = e.Seq;

			var s = _state;

			bool isDown = e.Action == KeyAction.Down;
			switch (e.Key)
			{
				case KeyKind.Ctrl: s = s with { Ctrl = isDown }; break;
				case KeyKind.Alt: s = s with { Alt = isDown }; break;
				case KeyKind.Shift: s = s with { Shift = isDown }; break;
				case KeyKind.Win: s = s with { Win = isDown }; break;
				default:
					// non-modifier keys do not change modifier state
					break;
			}

			if (!s.Equals(_state))
			{
				_state = s;
				newState = s;
			}
		}

		if (newState is not null)
		{
			_log.Info(Cat, $"Modifiers updated: {newState.Value}");
			Changed?.Invoke(newState.Value);
		}
	}

	public void Reset(string reason)
	{
		lock (_gate)
		{
			_state = new ModifierState(false, false, false, false);
			_lastSeq = 0;
		}

		_log.Warn(Cat, $"Modifiers reset: {reason}");
		Changed?.Invoke(Current);
	}
}
