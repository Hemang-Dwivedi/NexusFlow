namespace NexusFlow.Protocol.Input;

public sealed record KeyPayloadV1(
	int VkCode,
	int ScanCode,
	bool IsDown,
	bool IsExtended
);
