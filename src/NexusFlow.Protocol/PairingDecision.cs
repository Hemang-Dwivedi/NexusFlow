namespace NexusFlow.Protocol.Pairing;

public sealed record PairingDecision(
	Guid SessionId,
	bool Accepted
);
