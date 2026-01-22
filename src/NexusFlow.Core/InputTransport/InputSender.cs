using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using NexusFlow.Identity;
using NexusFlow.Protocol.Input;
using NexusFlow.Protocol.Transport;
using NexusFlow.Transport;

namespace NexusFlow.Core.InputTransport;

public sealed class InputSender : IDisposable
{
	private readonly ILocalIdentity _me;

	private readonly SemaphoreSlim _gate = new(1, 1);

	private TcpClient? _client;
	private NetworkStream? _stream;

	private string? _host;
	private int _port;

	private long _seq;

	public InputSender(ILocalIdentity me)
	{
		_me = me;
	}

	public long NextSeq() => Interlocked.Increment(ref _seq);

	public async Task EnsureConnectedAsync(string targetIpOrHost, int port, CancellationToken ct)
	{
		// Remember the latest endpoint (so reconnect after failure can use it)
		_host = targetIpOrHost;
		_port = port;

		await _gate.WaitAsync(ct).ConfigureAwait(false);
		try
		{
			if (IsUsableConnectedSocket(_client, _stream))
				return;

			await ReconnectLockedAsync(ct).ConfigureAwait(false);
		}
		finally
		{
			_gate.Release();
		}
	}

	public async Task SendAsync(InputEventV1 ev, CancellationToken ct)
	{
		// Fast path attempt: try writing under lock so stream doesn't change mid-write
		await _gate.WaitAsync(ct).ConfigureAwait(false);
		try
		{
			if (!IsUsableConnectedSocket(_client, _stream))
			{
				await ReconnectLockedAsync(ct).ConfigureAwait(false);
			}

			// At this point stream must exist or reconnect threw
			await FramingV2.WriteAsync(_stream!, MessageType.Input, InputCodec.Encode(ev), ct).ConfigureAwait(false);
		}
		catch
		{
			// Mark dead and let the next call reconnect.
			ResetLocked();
			throw;
		}
		finally
		{
			_gate.Release();
		}
	}

	private async Task ReconnectLockedAsync(CancellationToken ct)
	{
		if (string.IsNullOrWhiteSpace(_host))
			throw new InvalidOperationException("InputSender: No target host set. Call EnsureConnectedAsync first.");

		ResetLocked();

		var client = new TcpClient
		{
			NoDelay = true // low latency
		};

		await client.ConnectAsync(_host!, _port, ct).ConfigureAwait(false);
		var stream = client.GetStream();

		_client = client;
		_stream = stream;

		// Always send hello as the FIRST framed Input message on this socket.
		var hello = new InputHelloV1(_me.PeerId, DateTime.UtcNow.Ticks);
		await FramingV2.WriteAsync(stream, MessageType.Input, InputCodec.Encode(hello), ct).ConfigureAwait(false);
	}

	private static bool IsUsableConnectedSocket(TcpClient? c, NetworkStream? s)
	{
		if (c is null || s is null) return false;
		if (!c.Connected) return false;

		// This is the only reliable "did the peer close?" check without a read:
		// Poll + Available==0 indicates graceful close.
		try
		{
			var sock = c.Client;
			if (sock is null) return false;
			if (sock.Poll(0, SelectMode.SelectRead) && sock.Available == 0)
				return false;
		}
		catch
		{
			return false;
		}

		return true;
	}

	private void ResetLocked()
	{
		try { _stream?.Dispose(); } catch { }
		try { _client?.Close(); } catch { }
		_stream = null;
		_client = null;
	}

	public void Dispose()
	{
		_gate.Wait();
		try
		{
			ResetLocked();
		}
		finally
		{
			_gate.Release();
			_gate.Dispose();
		}
	}
}
