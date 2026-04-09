namespace NexusFlow.Protocol.Input;

public enum InputKind : byte
{
	Key = 1,
	MouseMove = 2,
	MouseButton = 3,
	MouseWheel = 4
}

// All types are readonly record structs so the input pipeline carries events
// by value through the Channel — no per-event heap allocations on sender or receiver.

public readonly record struct InputEventV1(
	string FromPeerId,
	long Seq,
	long TimestampUtcTicks,
	InputKind Kind,
	InputKeyPayload? Key = null,
	InputMouseMovePayload? Move = null,
	InputMouseButtonPayload? Button = null,
	InputMouseWheelPayload? Wheel = null
);

public readonly record struct InputKeyPayload(int VkCode, int ScanCode, bool IsDown);
public readonly record struct InputMouseMovePayload(int Dx, int Dy, int X, int Y);
public readonly record struct InputMouseButtonPayload(byte Button /*L=1,R=2,M=3*/, bool IsDown, int X, int Y);
public readonly record struct InputMouseWheelPayload(int Delta, int X, int Y);
