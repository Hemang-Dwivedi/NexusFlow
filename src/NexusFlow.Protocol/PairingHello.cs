namespace NexusFlow.Protocol.Pairing;

public sealed record PairingHello(
	string PeerId,
	string DeviceName,
	Guid SessionId,
	byte[] EcdhPublicKey,  // exported
	byte[] Nonce,          // 16 bytes
	int ProtocolVersion
);
