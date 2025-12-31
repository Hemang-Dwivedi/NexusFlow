namespace NexusFlow.Core.Input;

public enum InputEventKind
{
	Key,
	MouseMove,
	MouseClick,
	MouseScroll,
	MicActivity
}

public interface IOrderedInputEvent
{
	InputEventKind Kind { get; }
	string FromPeerId { get; }
	long Seq { get; }                 // per-peer sequence number
	DateTimeOffset Timestamp { get; } // for diagnostics only
}

public sealed record OrderedKeyEvent(
	string FromPeerId,
	long Seq,
	DateTimeOffset Timestamp,
	KeyKind Key,
	KeyAction Action
) : IOrderedInputEvent
{
	public InputEventKind Kind => InputEventKind.Key;
}

public sealed record OrderedMouseMoveEvent(
	string FromPeerId,
	long Seq,
	DateTimeOffset Timestamp,
	double Dx,
	double Dy
) : IOrderedInputEvent
{
	public InputEventKind Kind => InputEventKind.MouseMove;
}

public sealed record OrderedMouseClickEvent(
	string FromPeerId,
	long Seq,
	DateTimeOffset Timestamp
) : IOrderedInputEvent
{
	public InputEventKind Kind => InputEventKind.MouseClick;
}

public sealed record OrderedMouseScrollEvent(
	string FromPeerId,
	long Seq,
	DateTimeOffset Timestamp,
	double Delta
) : IOrderedInputEvent
{
	public InputEventKind Kind => InputEventKind.MouseScroll;
}

public sealed record OrderedMicActivityEvent(
	string FromPeerId,
	long Seq,
	DateTimeOffset Timestamp
) : IOrderedInputEvent
{
	public InputEventKind Kind => InputEventKind.MicActivity;
}
