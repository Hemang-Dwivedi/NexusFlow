namespace NexusFlow.Input;

public enum CapturedKeyAction { Down, Up }

// All captured event types are readonly record structs so hook callbacks
// never allocate on the heap for the most common events (mouse moves).

public readonly record struct CapturedKeyEvent(
	int VkCode,
	int ScanCode,
	int Flags,
	CapturedKeyAction Action,
	long TimestampUtcTicks
);

public readonly record struct CapturedMouseMoveEvent(
	int Dx, int Dy,
	int X, int Y,
	long TimestampUtcTicks
);

public enum CapturedMouseButton { Left, Right, Middle }
public enum MouseButtonAction { Down, Up }

public readonly record struct CapturedMouseButtonEvent(
	CapturedMouseButton Button,
	MouseButtonAction Action,
	int X, int Y,
	long TimestampUtcTicks
);

public readonly record struct CapturedMouseWheelEvent(
	int Delta,
	int X, int Y,
	long TimestampUtcTicks
);

public enum CapturedInputKind
{
	Key,
	MouseMove,
	MouseButton,
	MouseWheel
}
