using NexusFlow.Identity;
using NexusFlow.Protocol.Pairing;
using NexusFlow.Protocol.Transport;
using NexusFlow.Transport;
using NexusFlow.Trust;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace NexusFlow.Core.Trust;

public sealed class PairingListener
{
	private readonly ILocalIdentity _me;

	public event Action<IncomingPairingSession>? IncomingPairing;

	public PairingListener(ILocalIdentity me)
	{
		_me = me;
	}

	// Called by TcpMuxHost when the first frame type == Pairing.
	// firstPayload is the decoded payload of PairingHello (WITHOUT the type/len header).
	public async Task HandleIncomingAsync(TcpClient client, NetworkStream stream, byte[] firstPayload, CancellationToken ct)
	{
		using var ecdh = Ecdh.Create();
		var nonce = RandomNumberGenerator.GetBytes(16);

		// 1) Decode initiator hello from mux-provided first payload
		PairingHello initiatorHello;
		try
		{
			initiatorHello = PairingCodec.Decode<PairingHello>(firstPayload) ?? throw new InvalidOperationException("Bad hello.");
		}
		catch
		{
			try { client.Close(); } catch { }
			return;
		}

		// 2) Send our hello (same session id)
		var myHello = new PairingHello(
			PeerId: _me.PeerId,
			DeviceName: _me.DeviceName,
			SessionId: initiatorHello.SessionId,
			EcdhPublicKey: Ecdh.ExportPublic(ecdh),
			Nonce: nonce,
			ProtocolVersion: 1
		);

		try
		{
			await FramingV2.WriteAsync(stream, MessageType.Pairing, PairingCodec.Encode(myHello), ct);
		}
		catch
		{
			try { client.Close(); } catch { }
			return;
		}

		// 3) Compute shared + transcript + code/fingerprint
		string code;
		string fingerprint;

		try
		{
			using var initiatorPub = Ecdh.ImportPublic(initiatorHello.EcdhPublicKey);
			var shared = ecdh.DeriveKeyMaterial(initiatorPub);

			var transcript = PairingCoordinator.BuildTranscriptStatic(myHello, initiatorHello);
			code = Sas.Compute6DigitCode(shared, transcript);
			fingerprint = Sas.Fingerprint(shared, transcript);
		}
		catch
		{
			try { client.Close(); } catch { }
			return;
		}

		// 4) Hand off to UI as a session (session will continue on same stream)
		IncomingPairing?.Invoke(new IncomingPairingSession(
			initiatorHello.SessionId,
			initiatorHello.PeerId,
			initiatorHello.DeviceName,
			code,
			fingerprint,
			client
		));

		await Task.CompletedTask;
	}
}

public sealed class IncomingPairingSession
{
	private readonly TcpClient _client;
	private readonly NetworkStream _stream;

	public Guid SessionId { get; }
	public string RemotePeerId { get; }
	public string RemoteDeviceName { get; }
	public string Code6Digits { get; }
	public string Fingerprint { get; }

	internal IncomingPairingSession(Guid id, string remotePeerId, string remoteName, string code, string fingerprint, TcpClient client)
	{
		SessionId = id;
		RemotePeerId = remotePeerId;
		RemoteDeviceName = remoteName;
		Code6Digits = code;
		Fingerprint = fingerprint;
		_client = client;
		_stream = client.GetStream();
	}

	public Task SendDecisionAsync(bool accepted, CancellationToken ct)
		=> FramingV2.WriteAsync(_stream, MessageType.Pairing, PairingCodec.Encode(new PairingDecision(SessionId, accepted)), ct);

	public async Task<PairingDecision> WaitDecisionAsync(CancellationToken ct)
	{
		var (type, payload) = await FramingV2.ReadAsync(_stream, ct);
		if (type != MessageType.Pairing) throw new InvalidOperationException("Unexpected message type.");
		return PairingCodec.Decode<PairingDecision>(payload) ?? throw new InvalidOperationException("Bad decision.");
	}

	public void Close()
	{
		try { _client.Close(); } catch { }
	}
}
