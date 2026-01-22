namespace NexusFlow.Protocol.Input;

public sealed record MouseWheelPayloadV1(
	int Delta,
	bool IsHorizontal,
	int X,
	int Y
);