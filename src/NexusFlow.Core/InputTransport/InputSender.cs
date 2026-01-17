using System.Net.Sockets;
using NexusFlow.Core.Routing;
using NexusFlow.Identity;
using NexusFlow.Protocol.Input;
using NexusFlow.Protocol.Transport;
using NexusFlow.Transport;

namespace NexusFlow.Core.InputTransport;

public sealed class InputSender : IDisposable
{
	private readonly ILocalIdentity _me;
	private readonly IRoutingEngine _routing;

	private TcpClient? _client;
	private NetworkStream? _stream;
	private long _seq;

	public InputSender(ILocalIdentity me, IRoutingEngine routing)
	{
		_me = me;
		_routing = routing;
	}

	public async Task EnsureConnectedAsync(string targetIpOrHost, int port, CancellationToken ct)
	{
		if (_client is not null && _client.Connected) return;

		_client?.Close();
		_client = new TcpClient();
		await _client.ConnectAsync(targetIpOrHost, port, ct).ConfigureAwait(false);
		_stream = _client.GetStream();

		var hello = new InputHelloV1(_me.PeerId, DateTime.UtcNow.Ticks);
		await FramingV2.WriteAsync(_stream, MessageType.Input, InputCodec.Encode(hello), ct);
	}

	public async Task SendAsync(InputEventV1 ev, CancellationToken ct)
	{
		if (_stream is null) return;
		await FramingV2.WriteAsync(_stream, MessageType.Input, InputCodec.Encode(ev), ct).ConfigureAwait(false);
	}

	public long NextSeq() => Interlocked.Increment(ref _seq);

	public void Dispose()
	{
		try { _stream?.Dispose(); } catch { }
		try { _client?.Close(); } catch { }
		_stream = null;
		_client = null;
	}
}
