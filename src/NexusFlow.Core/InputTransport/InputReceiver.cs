using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using NexusFlow.Core.Diagnostics;
using NexusFlow.Protocol.Input;
using NexusFlow.Protocol.Transport;
using NexusFlow.Transport;
using NexusFlow.Trust;

namespace NexusFlow.Core.InputTransport;

public sealed class InputReceiver
{
	private const string Cat = "input-remote";

	private readonly IDiagnosticsLog _log;
	private readonly TrustStore _trust;
	private readonly IInputAuthKeyProvider _keys;

	public InputReceiver(IDiagnosticsLog log, TrustStore trust, IInputAuthKeyProvider keys)
	{
		_log = log;
		_trust = trust;
		_keys = keys;
	}

	public async Task HandleFirstFrameAsync(TcpClient client, NetworkStream stream, byte[] firstPayload, CancellationToken ct)
	{
		InputHelloV2 hello;
		try
		{
			hello = InputCodec.Decode<InputHelloV2>(firstPayload);
		}
		catch
		{
			try { client.Close(); } catch { }
			return;
		}

		// ---- F.4: TRUST ENFORCEMENT ----
		if (!IsTrustedPeer(hello.FromPeerId))
		{
			_log.Warn(Cat, $"Reject INPUT: untrusted peerId={hello.FromPeerId}");
			try { client.Close(); } catch { }
			return;
		}

		// ---- F.5: MUST have an active authenticated control-session derived key ----
		if (!_keys.TryGetInputAuthKey(hello.FromPeerId, out var inputAuthKey))
		{
			_log.Warn(Cat, $"Reject INPUT: no active control session key for peerId={hello.FromPeerId}");
			try { client.Close(); } catch { }
			return;
		}

		// ---- F.5: validate hello MAC ----
		var expectedMac = ComputeHelloMac(inputAuthKey, hello.FromPeerId, hello.TimestampUtcTicks, hello.Nonce);
		if (hello.Mac is null || hello.Mac.Length == 0 || !CryptographicOperations.FixedTimeEquals(expectedMac, hello.Mac))
		{
			_log.Warn(Cat, $"Reject INPUT: bad MAC from peerId={hello.FromPeerId}");
			try { client.Close(); } catch { }
			return;
		}

		_log.Info(Cat, $"RX input channel AUTHENTICATED from {hello.FromPeerId}");

		try
		{
			while (!ct.IsCancellationRequested)
			{
				var (type, payload) = await FramingV2.ReadAsync(stream, ct).ConfigureAwait(false);
				if (type != MessageType.Input)
					continue;

				var ev = InputCodec.Decode<InputEventV1>(payload);

				switch (ev.Kind)
				{
					case InputKind.Key:
						_log.Trace(Cat, $"RX KEY vk={ev.Key!.VkCode} down={ev.Key.IsDown}");
						break;

					case InputKind.MouseMove:
						_log.Trace(Cat, $"RX MOVE dx={ev.Move!.Dx} dy={ev.Move.Dy}");
						break;

					case InputKind.MouseButton:
						_log.Trace(Cat, $"RX BTN {ev.Button!.Button} down={ev.Button.IsDown}");
						break;

					case InputKind.MouseWheel:
						_log.Trace(Cat, $"RX WHEEL delta={ev.Wheel!.Delta}");
						break;

					default:
						_log.Trace(Cat, $"RX {ev.FromPeerId} seq={ev.Seq} kind={ev.Kind}");
						break;
				}
			}
		}
		catch
		{
			// drop
		}
		finally
		{
			try { client.Close(); } catch { }
			_log.Info(Cat, $"RX input channel closed from {hello.FromPeerId}");
		}
	}

	private bool IsTrustedPeer(string peerId)
	{
		var state = _trust.Load();

		// Your existing trust criterion looks wrong (TrustedAtUtc <= now doesn't mean "trusted").
		// Use whatever your actual model is; safest minimal check is "peer exists in store".
		// If you have an explicit flag, use it.
		return state.Peers.Any(p => p.PeerId == peerId);
	}

	private static byte[] ComputeHelloMac(byte[] key, string fromPeerId, long tsTicks, byte[] nonce)
	{
		using var h = new HMACSHA256(key);

		var idBytes = Encoding.UTF8.GetBytes(fromPeerId);
		var tsBytes = BitConverter.GetBytes(tsTicks);
		var msg = new byte[idBytes.Length + 1 + tsBytes.Length + nonce.Length];

		Buffer.BlockCopy(idBytes, 0, msg, 0, idBytes.Length);
		msg[idBytes.Length] = 0;
		Buffer.BlockCopy(tsBytes, 0, msg, idBytes.Length + 1, tsBytes.Length);
		Buffer.BlockCopy(nonce, 0, msg, idBytes.Length + 1 + tsBytes.Length, nonce.Length);

		return h.ComputeHash(msg);
	}
}
