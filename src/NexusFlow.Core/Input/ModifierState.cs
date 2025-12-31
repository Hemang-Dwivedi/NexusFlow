namespace NexusFlow.Core.Input;

public readonly record struct ModifierState(bool Ctrl, bool Alt, bool Shift, bool Win)
{
	public override string ToString()
		=> $"Ctrl={(Ctrl ? 1 : 0)} Alt={(Alt ? 1 : 0)} Shift={(Shift ? 1 : 0)} Win={(Win ? 1 : 0)}";
}
