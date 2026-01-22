using System.Net.Sockets;
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

	public InputReceiver(IDiagnosticsLog log, TrustStore trust)
	{
		_log = log;
		_trust = trust;
	}

	public async Task HandleFirstFrameAsync(TcpClient client, NetworkStream stream, byte[] firstPayload, CancellationToken ct)
	{
		InputHelloV1 hello;
		try
		{
			hello = InputCodec.Decode<InputHelloV1>(firstPayload);
		}
		catch
		{
			try { client.Close(); } catch { }
			return;
		}

		// ---- F.4: TRUST ENFORCEMENT ----
		if (!IsTrustedPeer(hello.FromPeerId))
		{
			_log.Warn(Cat, $"Rejecting INPUT channel from untrusted peerId={hello.FromPeerId}");
			try { client.Close(); } catch { }
			return;
		}

		_log.Info(Cat, $"RX input channel opened from {hello.FromPeerId}");

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
		// Load once per connection (fine for F.4).
		// Later we can cache and update on trust changes.
		var state = _trust.Load();
		return state.Peers.Any(p => p.PeerId == peerId && p.TrustedAtUtc <= DateTime.UtcNow);
	}
}
