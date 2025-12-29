using System.Net;
using System.Net.Sockets;
using NexusFlow.Discovery.Peers;
using NexusFlow.Protocol.Discovery;

namespace NexusFlow.Discovery.Udp;

public sealed class DiscoveryListener : IDisposable
{
	private readonly UdpClient _udp;
	private readonly PeerRegistry _registry;

	private CancellationTokenSource? _cts;
	private Task? _loop;

	public DiscoveryListener(PeerRegistry registry)
	{
		_registry = registry;

		// Bind to all interfaces on the discovery port
		_udp = new UdpClient(new IPEndPoint(IPAddress.Any, DiscoveryProtocol.UdpPort))
		{
			EnableBroadcast = true
		};
	}

	public void Start()
	{
		if (_cts is not null) return;
		_cts = new CancellationTokenSource();
		_loop = RunAsync(_cts.Token);
	}

	public async Task StopAsync()
	{
		if (_cts is null) return;
		_cts.Cancel();
		try { if (_loop is not null) await _loop; } catch { /* swallow */ }
		_cts.Dispose();
		_cts = null;
		_loop = null;
	}

	private async Task RunAsync(CancellationToken ct)
	{
		while (!ct.IsCancellationRequested)
		{
			UdpReceiveResult result;
			try
			{
				result = await _udp.ReceiveAsync(ct);
			}
			catch (OperationCanceledException)
			{
				break;
			}

			if (!HelloCodec.TryDecode(result.Buffer, out var hello) || hello is null)
				continue;

			// Optional: ignore self in Core by checking PeerId; listener stays pure.
			var now = DateTimeOffset.UtcNow;
			_registry.ObserveHello(
				peerId: hello.PeerId,
				deviceName: hello.DeviceName,
				tcpPort: hello.TcpPort,
				protocolVersion: hello.ProtocolVersion,
				now: now
			);
		}
	}

	public void Dispose()
	{
		_ = StopAsync();
		_udp.Dispose();
	}
}
