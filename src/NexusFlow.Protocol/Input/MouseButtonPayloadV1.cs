namespace NexusFlow.Protocol.Input;

public enum MouseButtonV1
{
	Left,
	Right,
	Middle,
	X1,
	X2
}

public sealed record MouseButtonPayloadV1(
	MouseButtonV1 Button,
	bool IsDown,
	int X,
	int Y
);
