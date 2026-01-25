using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NexusFlow.Core.InputTransport;
using NexusFlow.Identity;
using NexusFlow.Protocol.Input;
using NexusFlow.Protocol.Transport;
using NexusFlow.Transport;

namespace NexusFlow.Core.InputTransport;

public sealed class InputSender : IDisposable
{
	private readonly ILocalIdentity _me;
	private readonly IInputAuthKeyProvider _keys;

	private readonly SemaphoreSlim _gate = new(1, 1);

	private TcpClient? _client;
	private NetworkStream? _stream;

	private string? _host;
	private int _port;

	private string? _targetPeerId;

	private long _seq;

	public InputSender(ILocalIdentity me, IInputAuthKeyProvider keys)
	{
		_me = me;
		_keys = keys;
	}

	public long NextSeq() => Interlocked.Increment(ref _seq);

	public async Task EnsureConnectedAsync(string targetPeerId, string targetIpOrHost, int port, CancellationToken ct)
	{
		_targetPeerId = targetPeerId;
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
		await _gate.WaitAsync(ct).ConfigureAwait(false);
		try
		{
			if (!IsUsableConnectedSocket(_client, _stream))
				await ReconnectLockedAsync(ct).ConfigureAwait(false);

			await FramingV2.WriteAsync(_stream!, MessageType.Input, InputCodec.Encode(ev), ct).ConfigureAwait(false);
		}
		catch
		{
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
		if (string.IsNullOrWhiteSpace(_host) || _port == 0 || string.IsNullOrWhiteSpace(_targetPeerId))
			throw new InvalidOperationException("InputSender: missing endpoint/peerId. Call EnsureConnectedAsync(targetPeerId, host, port) first.");

		// Must have auth key derived from authenticated Control session
		if (!_keys.TryGetInputAuthKey(_targetPeerId!, out var inputAuthKey))
			throw new InvalidOperationException($"InputSender: no InputAuthKey for peerId={_targetPeerId} (no authenticated control session?)");

		ResetLocked();

		var client = new TcpClient { NoDelay = true };
		await client.ConnectAsync(_host!, _port, ct).ConfigureAwait(false);
		var stream = client.GetStream();

		_client = client;
		_stream = stream;

		// F.5: Authenticated hello FIRST
		var hello = BuildHelloV2(inputAuthKey);
		await FramingV2.WriteAsync(stream, MessageType.Input, InputCodec.Encode(hello), ct).ConfigureAwait(false);
	}

	private InputHelloV2 BuildHelloV2(byte[] inputAuthKey)
	{
		var ts = DateTime.UtcNow.Ticks;
		var nonce = RandomNumberGenerator.GetBytes(16);
		var mac = ComputeHelloMac(inputAuthKey, _me.PeerId, ts, nonce);
		return new InputHelloV2(_me.PeerId, ts, nonce, mac);
	}

	private static byte[] ComputeHelloMac(byte[] key, string fromPeerId, long tsTicks, byte[] nonce)
	{
		using var h = new HMACSHA256(key);

		var idBytes = Encoding.UTF8.GetBytes(fromPeerId);
		var tsBytes = BitConverter.GetBytes(tsTicks); // little-endian
		var msg = new byte[idBytes.Length + 1 + tsBytes.Length + nonce.Length];

		Buffer.BlockCopy(idBytes, 0, msg, 0, idBytes.Length);
		msg[idBytes.Length] = 0;
		Buffer.BlockCopy(tsBytes, 0, msg, idBytes.Length + 1, tsBytes.Length);
		Buffer.BlockCopy(nonce, 0, msg, idBytes.Length + 1 + tsBytes.Length, nonce.Length);

		return h.ComputeHash(msg);
	}

	private static bool IsUsableConnectedSocket(TcpClient? c, NetworkStream? s)
	{
		if (c is null || s is null) return false;
		if (!c.Connected) return false;

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
		try { ResetLocked(); }
		finally
		{
			_gate.Release();
			_gate.Dispose();
		}
	}
}
