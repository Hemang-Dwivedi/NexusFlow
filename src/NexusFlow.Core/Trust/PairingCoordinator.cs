using NexusFlow.Identity;
using NexusFlow.Protocol.Pairing;
using NexusFlow.Protocol.Transport;
using NexusFlow.Transport;
using NexusFlow.Trust;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace NexusFlow.Core.Trust;

public sealed class PairingCoordinator
{
	private readonly ILocalIdentity _me;

	public PairingCoordinator(ILocalIdentity me)
	{
		_me = me;
	}

	public async Task<PairingSession> BeginPairingAsync(
		IPAddress peerAddress,
		int peerPort,
		CancellationToken ct)
	{
		var client = new TcpClient(AddressFamily.InterNetwork);
		await client.ConnectAsync(peerAddress, peerPort, ct);
		var stream = client.GetStream();

		using var ecdh = Ecdh.Create();
		var nonce = RandomNumberGenerator.GetBytes(16);
		var sessionId = Guid.NewGuid();

		var hello = new PairingHello(
			PeerId: _me.PeerId,
			DeviceName: _me.DeviceName,
			SessionId: sessionId,
			EcdhPublicKey: Ecdh.ExportPublic(ecdh),
			Nonce: nonce,
			ProtocolVersion: 1
		);

		await FramingV2.WriteAsync(
			stream,
			MessageType.Pairing,
			PairingCodec.Encode(hello),
			ct
		);

		// receive remote hello
		var (type, remoteHelloBytes) = await FramingV2.ReadAsync(stream, ct);
		if (type != MessageType.Pairing)
			throw new InvalidOperationException("Unexpected message type");

		var remoteHello = PairingCodec.Decode<PairingHello>(remoteHelloBytes)
			?? throw new InvalidOperationException("Invalid remote hello");

		// compute shared secret + code + fingerprint
		using var remotePub = Ecdh.ImportPublic(remoteHello.EcdhPublicKey);
		var shared = ecdh.DeriveKeyMaterial(remotePub);
		var transcript = BuildTranscript(hello, remoteHello);

		var code = Sas.Compute6DigitCode(shared, transcript);
		var fingerprint = Sas.Fingerprint(shared, transcript);

		return new PairingSession(
			sessionId,
			remoteHello.PeerId,
			remoteHello.DeviceName,
			code,
			fingerprint,
			stream,
			client
		);
	}


	private static byte[] BuildTranscript(PairingHello a, PairingHello b)
	{
		// Deterministic ordering by PeerId ensures both sides hash the same bytes
		var first = string.CompareOrdinal(a.PeerId, b.PeerId) <= 0 ? a : b;
		var second = ReferenceEquals(first, a) ? b : a;

		using var ms = new MemoryStream();
		void W(string s) { var bytes = System.Text.Encoding.UTF8.GetBytes(s); ms.Write(bytes); ms.WriteByte(0); }
		void WB(byte[] x) { ms.Write(x); ms.WriteByte(0); }

		W(first.PeerId); W(first.DeviceName); W(first.SessionId.ToString());
		WB(first.EcdhPublicKey); WB(first.Nonce);

		W(second.PeerId); W(second.DeviceName); W(second.SessionId.ToString());
		WB(second.EcdhPublicKey); WB(second.Nonce);

		return ms.ToArray();
	}

	public static byte[] BuildTranscriptStatic(PairingHello a, PairingHello b)
	{
		var first = string.CompareOrdinal(a.PeerId, b.PeerId) <= 0 ? a : b;
		var second = ReferenceEquals(first, a) ? b : a;

		using var ms = new MemoryStream();
		void W(string s) { var bytes = System.Text.Encoding.UTF8.GetBytes(s); ms.Write(bytes); ms.WriteByte(0); }
		void WB(byte[] x) { ms.Write(x); ms.WriteByte(0); }

		W(first.PeerId); W(first.DeviceName); W(first.SessionId.ToString());
		WB(first.EcdhPublicKey); WB(first.Nonce);

		W(second.PeerId); W(second.DeviceName); W(second.SessionId.ToString());
		WB(second.EcdhPublicKey); WB(second.Nonce);

		return ms.ToArray();
	}

}

public sealed class PairingSession
{
	public Guid SessionId { get; }
	public string RemotePeerId { get; }
	public string RemoteDeviceName { get; }
	public string Code6Digits { get; }
	public string Fingerprint { get; }

	internal NetworkStream Stream { get; }
	internal TcpClient Client { get; }

	internal PairingSession(Guid id, string remotePeerId, string remoteName, string code, string fingerprint, NetworkStream stream, TcpClient client)
	{
		SessionId = id;
		RemotePeerId = remotePeerId;
		RemoteDeviceName = remoteName;
		Code6Digits = code;
		Fingerprint = fingerprint;
		Stream = stream;
		Client = client;
	}

	public Task SendDecisionAsync(bool accepted, CancellationToken ct)
	=> FramingV2.WriteAsync(
		Stream,
		MessageType.Pairing,
		PairingCodec.Encode(new PairingDecision(SessionId, accepted)),
		ct
	);
	public async Task<PairingDecision> WaitDecisionAsync(CancellationToken ct)
	{
		var (_, bytes) = await FramingV2.ReadAsync(Stream, ct);
		return PairingCodec.Decode<PairingDecision>(bytes) ?? throw new InvalidOperationException("Invalid decision.");
	}

	public void Close() { try { Client.Close(); } catch { } }
}
