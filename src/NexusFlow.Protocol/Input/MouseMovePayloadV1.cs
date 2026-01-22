namespace NexusFlow.Protocol.Input;

public sealed record MouseMovePayloadV1(
	int Dx,
	int Dy,
	int X,
	int Y,
	bool IsAbsolute
);
