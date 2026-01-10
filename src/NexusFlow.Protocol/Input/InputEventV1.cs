namespace NexusFlow.Protocol.Input;

public enum InputKind : byte
{
	Key = 1,
	MouseMove = 2,
	MouseButton = 3,
	MouseWheel = 4
}

public sealed record InputEventV1(
	string FromPeerId,
	long Seq,
	long TimestampUtcTicks,
	InputKind Kind,
	InputKeyPayload? Key = null,
	InputMouseMovePayload? Move = null,
	InputMouseButtonPayload? Button = null,
	InputMouseWheelPayload? Wheel = null
);

public sealed record InputKeyPayload(int VkCode, int ScanCode, bool IsDown);
public sealed record InputMouseMovePayload(int Dx, int Dy, int X, int Y);
public sealed record InputMouseButtonPayload(byte Button /*L=1,R=2,M=3*/, bool IsDown, int X, int Y);
public sealed record InputMouseWheelPayload(int Delta, int X, int Y);
