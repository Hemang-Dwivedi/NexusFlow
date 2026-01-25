using System.Net.Sockets;
using NexusFlow.Core.Diagnostics;
using NexusFlow.Core.InputInjection;
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
	private readonly IInputInjector _injector;

	public InputReceiver(
		IDiagnosticsLog log,
		TrustStore trust,
		IInputInjector injector)
	{
		_log = log;
		_trust = trust;
		_injector = injector;
	}

	public async Task HandleFirstFrameAsync(
		TcpClient client,
		NetworkStream stream,
		byte[] firstPayload,
		CancellationToken ct)
	{
		InputHelloV1 hello;
		try
		{
			hello = InputCodec.Decode<InputHelloV1>(firstPayload);
		}
		catch
		{
			client.Close();
			return;
		}

		// ---- F.4 trust enforcement (already done) ----
		if (!IsTrustedPeer(hello.FromPeerId))
		{
			_log.Warn(Cat, $"Rejecting INPUT from untrusted peer={hello.FromPeerId}");
			client.Close();
			return;
		}

		_log.Info(Cat, $"RX input channel opened from {hello.FromPeerId}");

		try
		{
			while (!ct.IsCancellationRequested)
			{
				var (type, payload) = await FramingV2.ReadAsync(stream, ct);
				if (type != MessageType.Input)
					continue;

				var ev = InputCodec.Decode<InputEventV1>(payload);

				// ---- F.7 injection boundary ----
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

	private bool IsTrustedPeer(string peerId)
	{
		var state = _trust.Load();
		return state.Peers.Any(p => p.PeerId == peerId && p.TrustedAtUtc <= DateTime.UtcNow);
	}
}
