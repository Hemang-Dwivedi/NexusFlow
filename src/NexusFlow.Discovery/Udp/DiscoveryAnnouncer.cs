using System.Net;
using System.Net.Sockets;
using NexusFlow.Protocol.Discovery;

namespace NexusFlow.Discovery.Udp;

public sealed class DiscoveryAnnouncer : IDisposable
{
	private readonly UdpClient _udp;
	private readonly IPEndPoint _broadcastEp;
	private readonly Func<HelloBroadcast> _helloFactory;
	private readonly TimeSpan _interval;

	private CancellationTokenSource? _cts;
	private Task? _loop;

	public DiscoveryAnnouncer(Func<HelloBroadcast> helloFactory, TimeSpan interval)
	{
		_helloFactory = helloFactory;
		_interval = interval;

		_udp = new UdpClient(AddressFamily.InterNetwork);
		_udp.EnableBroadcast = true;

		_broadcastEp = new IPEndPoint(IPAddress.Broadcast, DiscoveryProtocol.UdpPort);
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
		using var timer = new PeriodicTimer(_interval);

		while (!ct.IsCancellationRequested)
		{
			var hello = _helloFactory();
			var bytes = HelloCodec.Encode(hello);

			// Fire-and-forget send, but await to avoid flooding.
			await _udp.SendAsync(bytes, bytes.Length, _broadcastEp);

			await timer.WaitForNextTickAsync(ct);
		}
	}

	public void Dispose()
	{
		_ = StopAsync();
		_udp.Dispose();
	}
}
