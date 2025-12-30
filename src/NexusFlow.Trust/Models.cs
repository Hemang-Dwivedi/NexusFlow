namespace NexusFlow.Trust;

public sealed record TrustedPeer(
	string PeerId,
	string DeviceName,
	string Fingerprint,          // stable string from pairing secret/transcript
	DateTimeOffset TrustedAtUtc
);

public sealed class TrustState
{
	public List<TrustedPeer> Peers { get; set; } = new();
}
