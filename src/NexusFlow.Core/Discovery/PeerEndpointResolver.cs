using NexusFlow.Discovery.Peers;

namespace NexusFlow.Core.Discovery;

public class PeerEndpointResolver : IPeerEndpointResolver
{
	private readonly PeerRegistry _registry;

	public PeerEndpointResolver(PeerRegistry registry)
	{
		_registry = registry;
	}

	public bool TryGetEndpoint(string peerId, out string host, out int port)
	{
		host = "";
		port = 0;

		// Snapshot is IReadOnlyCollection<DiscoveredPeer>
		var peer = _registry.Snapshot().FirstOrDefault(p => p.PeerId == peerId);
		if (peer is null) return false;

		if (peer.LastKnownAddress is null) return false;

		host = peer.LastKnownAddress.ToString();
		port = peer.TcpPort;
		return true;
	}
}
