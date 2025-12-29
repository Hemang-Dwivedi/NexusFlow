using NexusFlow.Discovery.Peers;
using NexusFlow.Discovery.Udp;
using NexusFlow.Identity;
using NexusFlow.Protocol.Discovery;

namespace NexusFlow.Core.Discovery;

public sealed class DiscoveryCoordinator : IDisposable
{
	private readonly ILocalIdentity _identity;
	private readonly PeerRegistry _registry;
	private readonly DiscoveryAnnouncer _announcer;
	private readonly DiscoveryListener _listener;

	public PeerRegistry Registry => _registry;

	public DiscoveryCoordinator(ILocalIdentity identity, int tcpPort)
	{
		_identity = identity;

		_registry = new PeerRegistry(
			expiry: TimeSpan.FromSeconds(3),
			sweepInterval: TimeSpan.FromMilliseconds(500)
		);

		_listener = new DiscoveryListener(_registry);

		_announcer = new DiscoveryAnnouncer(
			helloFactory: () => new HelloBroadcast(
				PeerId: _identity.PeerId,
				DeviceName: _identity.DeviceName,
				TcpPort: tcpPort,
				ProtocolVersion: DiscoveryProtocol.ProtocolVersion,
				SentAtUnixMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
			),
			interval: TimeSpan.FromMilliseconds(750)
		);

		// Core-only self-filter (prevents showing yourself as a peer)
		_registry.OnPeerDiscovered += e =>
		{
			if (e.Peer.PeerId == _identity.PeerId) return;
			// Later: raise Core-level event / push into a state store.
		};
	}

	public void Start()
	{
		_listener.Start();
		_announcer.Start();
	}

	public Task StopAsync()
		=> Task.WhenAll(_listener.StopAsync(), _announcer.StopAsync());

	public void Dispose()
	{
		_announcer.Dispose();
		_listener.Dispose();
		_registry.Dispose();
	}
}
