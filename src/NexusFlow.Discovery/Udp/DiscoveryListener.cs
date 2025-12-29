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

		_udp = new UdpClient(AddressFamily.InterNetwork);

		// IMPORTANT on Windows: allow binding even if something else touched the port.
		_udp.Client.ExclusiveAddressUse = false;
		_udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);

		_udp.EnableBroadcast = true;

		// Bind AFTER setting options
		_udp.Client.Bind(new IPEndPoint(IPAddress.Any, DiscoveryProtocol.UdpPort));
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
		try { if (_loop is not null) await _loop; } catch { }
		_cts.Dispose();
		_cts = null;
		_loop = null;
	}

	private async Task RunAsync(CancellationToken ct)
	{
		while (!ct.IsCancellationRequested)
		{
			UdpReceiveResult result;
			try { result = await _udp.ReceiveAsync(ct); }
			catch (OperationCanceledException) { break; }

			if (!HelloCodec.TryDecode(result.Buffer, out var hello) || hello is null)
				continue;

			_registry.ObserveHello(
				hello.PeerId,
				hello.DeviceName,
				hello.TcpPort,
				hello.ProtocolVersion,
				DateTimeOffset.UtcNow
			);
		}
	}

	public void Dispose()
	{
		_ = StopAsync();
		_udp.Dispose();
	}
}
