using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using NexusFlow.Core.Control;              // IInputAuthKeyProvider
using NexusFlow.Core.Diagnostics;
using NexusFlow.Core.InputInjection;
using NexusFlow.Protocol.Input;
using NexusFlow.Protocol.Transport;
using NexusFlow.Transport;
using NexusFlow.Trust;
using NexusFlow.Core.Services;


namespace NexusFlow.Core.InputTransport;

public sealed class InputReceiver
{
	private const string Cat = "input-remote";

	private readonly IDiagnosticsLog _log;
	private readonly TrustStore _trust;
	private readonly IInputInjector _injector;
	private readonly IInputAuthKeyProvider _keys;
	private readonly IFailsafeService _failsafe;

	public InputReceiver(
		IDiagnosticsLog log,
		TrustStore trust,
		IInputInjector injector,
		IInputAuthKeyProvider keys,
		IFailsafeService failsafe)
	{
		_log = log;
		_trust = trust;
		_injector = injector;
		_keys = keys;
		_failsafe = failsafe;
	}

	public async Task HandleFirstFrameAsync(
		TcpClient client,
		NetworkStream stream,
		byte[] firstPayload,
		CancellationToken ct)
	{
		// ---- Expect authenticated hello (V2) ----
		InputHelloV2 hello;
		try
		{
			hello = InputCodec.Decode<InputHelloV2>(firstPayload);
		}
		catch
		{
			// If you want strict-only V2, just close. (Recommended)
			try { client.Close(); } catch { }
			return;
		}

		// ---- F.4 trust enforcement ----
		if (!IsTrustedPeer(hello.FromPeerId))
		{
			_log.Warn(Cat, $"Rejecting INPUT from untrusted peer={hello.FromPeerId}");
			try { client.Close(); } catch { }
			return;
		}

		// ---- F.5 authenticated hello enforcement ----
		// Must have an authenticated CONTROL session for this peer (key provider is ConnectionManager)
		if (!_keys.TryGetInputAuthKey(hello.FromPeerId, out var inputAuthKey) || inputAuthKey.Length == 0)
		{
			_log.Warn(Cat, $"Rejecting INPUT from peer={hello.FromPeerId}: no InputAuthKey (no authenticated control session?)");
			try { client.Close(); } catch { }
			return;
		}

		//if (!VerifyHelloMac(inputAuthKey, hello))
		//{
		//	_log.Warn(Cat, $"Rejecting INPUT from peer={hello.FromPeerId}: hello MAC invalid");
		//	try { client.Close(); } catch { }
		//	return;
		//}

		_log.Info(Cat, $"RX input channel opened from {hello.FromPeerId}");

		try
		{
			while (!ct.IsCancellationRequested)
			{
				var (type, payload) = await FramingV2.ReadAsync(stream, ct).ConfigureAwait(false);
				if (type != MessageType.Input)
					continue;

				var ev = InputCodec.Decode<InputEventV1>(payload);
				if (_failsafe.IsBlocked)
					continue; // local-only safety: never inject while failsafe ON

				_injector.Inject(ev);
			}
		}
		catch
		{
			// connection dropped
		}
		finally
		{
			try { client.Close(); } catch { }
			_injector.Reset();
			_log.Info(Cat, $"RX input channel closed from {hello.FromPeerId}");
		}
	}

	private static bool VerifyHelloMac(byte[] key, InputHelloV2 hello)
	{
		// Mac = HMAC-SHA256(key, FromPeerId || 0x00 || TimestampUtcTicks(le) || Nonce)
		using var h = new HMACSHA256(key);

		var idBytes = Encoding.UTF8.GetBytes(hello.FromPeerId);
		var tsBytes = BitConverter.GetBytes(hello.TimestampUtcTicks); // little-endian
		var nonce = hello.Nonce ?? Array.Empty<byte>();

		var msg = new byte[idBytes.Length + 1 + tsBytes.Length + nonce.Length];
		Buffer.BlockCopy(idBytes, 0, msg, 0, idBytes.Length);
		msg[idBytes.Length] = 0;
		Buffer.BlockCopy(tsBytes, 0, msg, idBytes.Length + 1, tsBytes.Length);
		Buffer.BlockCopy(nonce, 0, msg, idBytes.Length + 1 + tsBytes.Length, nonce.Length);

		var expected = h.ComputeHash(msg);
		return hello.Mac is not null && CryptographicOperations.FixedTimeEquals(expected, hello.Mac);
	}

	private bool IsTrustedPeer(string peerId)
	{
		var state = _trust.Load();
		return state.Peers.Any(p => p.PeerId == peerId && p.TrustedAtUtc <= DateTime.UtcNow);
	}
}
