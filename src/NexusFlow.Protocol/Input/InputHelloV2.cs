namespace NexusFlow.Protocol.Input;

/// <summary>
/// Authenticated input channel hello.
/// Mac = HMAC-SHA256(InputAuthKey, FromPeerId || 0x00 || TimestampUtcTicks(le) || Nonce)
/// </summary>
public sealed record InputHelloV2(
	string FromPeerId,
	long TimestampUtcTicks,
	byte[] Nonce,
	byte[] Mac
);
