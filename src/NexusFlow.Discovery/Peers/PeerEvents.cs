namespace NexusFlow.Discovery.Peers;

public sealed record PeerDiscovered(DiscoveredPeer Peer);
public sealed record PeerUpdated(DiscoveredPeer Peer);
public sealed record PeerLost(string PeerId);
