namespace NexusFlow.Input;

public enum CapturedKeyAction { Down, Up }

public sealed record CapturedKeyEvent(
	int VkCode,
	int ScanCode,
	CapturedKeyAction Action,
	long TimestampUtcTicks
);

public sealed record CapturedMouseMoveEvent(
	int Dx, int Dy,
	int X, int Y,
	long TimestampUtcTicks
);

public enum CapturedMouseButton { Left, Right, Middle }
public enum MouseButtonAction { Down, Up }

public sealed record CapturedMouseButtonEvent(
	CapturedMouseButton Button,
	MouseButtonAction Action,
	int X, int Y,
	long TimestampUtcTicks
);

public sealed record CapturedMouseWheelEvent(
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
