namespace NexusFlow.Core.Input;

public enum KeyKind
{
	A, B, C, D, E, F, G,
	Ctrl, Alt, Shift, Win,
	Esc, Enter, Tab, Space,
	Other
}

public enum KeyAction
{
	Down,
	Up
}

public sealed record SimKeyEvent(
	long Seq,
	string FromPeerId,
	KeyKind Key,
	KeyAction Action,
	DateTimeOffset Timestamp
);
