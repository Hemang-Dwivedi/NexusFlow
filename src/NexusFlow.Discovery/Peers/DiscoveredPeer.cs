namespace NexusFlow.Discovery.Peers;

public sealed record DiscoveredPeer(
	string PeerId,
	string DeviceName,
	int TcpPort,
	int ProtocolVersion,
	DateTimeOffset LastSeen
);
