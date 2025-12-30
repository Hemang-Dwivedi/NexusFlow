using System.Collections.Concurrent;

namespace NexusFlow.Discovery.Peers;

public sealed class PeerRegistry : IDisposable
{
	private readonly ConcurrentDictionary<string, DiscoveredPeer> _peers = new();
	private readonly TimeSpan _expiry;
	private readonly PeriodicTimer _timer;
	private readonly CancellationTokenSource _cts = new();

	public event Action<PeerDiscovered>? OnPeerDiscovered;
	public event Action<PeerUpdated>? OnPeerUpdated;
	public event Action<PeerLost>? OnPeerLost;

	public PeerRegistry(TimeSpan expiry, TimeSpan sweepInterval)
	{
		_expiry = expiry;
		_timer = new PeriodicTimer(sweepInterval);
		_ = RunExpiryLoopAsync(_cts.Token);
	}

	public IReadOnlyCollection<DiscoveredPeer> Snapshot()
		=> _peers.Values.ToArray();

	public void ObserveHello(string peerId, string deviceName, int tcpPort, int protocolVersion, DateTimeOffset now, System.Net.IPAddress? Address)
	{
		var incoming = new DiscoveredPeer(peerId, deviceName, tcpPort, protocolVersion, now, Address);

		if (_peers.TryAdd(peerId, incoming))
		{
			OnPeerDiscovered?.Invoke(new PeerDiscovered(incoming));
			return;
		}

		// Update if changed or just refresh last-seen
		_peers.AddOrUpdate(peerId, incoming, (_, existing) =>
		{
			var updated = existing with
			{
				DeviceName = deviceName,
				TcpPort = tcpPort,
				ProtocolVersion = protocolVersion,
				LastSeen = now
			};
			// Emit updated if anything meaningful changed OR if you want UI refresh ticks.
			if (updated.DeviceName != existing.DeviceName || updated.TcpPort != existing.TcpPort)
				OnPeerUpdated?.Invoke(new PeerUpdated(updated));
			else
				_peers[peerId] = updated;

			return updated;
		});
	}

	private async Task RunExpiryLoopAsync(CancellationToken ct)
	{
		try
		{
			while (await _timer.WaitForNextTickAsync(ct))
			{
				var now = DateTimeOffset.UtcNow;
				foreach (var kv in _peers)
				{
					if (now - kv.Value.LastSeen > _expiry)
					{
						if (_peers.TryRemove(kv.Key, out _))
							OnPeerLost?.Invoke(new PeerLost(kv.Key));
					}
				}
			}
		}
		catch (OperationCanceledException) { }
	}

	public void Dispose()
	{
		_cts.Cancel();
		_timer.Dispose();
		_cts.Dispose();
	}
}
