using NexusFlow.Identity;
using NexusFlow.Protocol.Pairing;
using NexusFlow.Transport;
using NexusFlow.Trust;
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

	public async Task<PairingSession> BeginPairingAsync(IPAddress peerAddress, int peerPort, CancellationToken ct)
	{
		using var ecdh = Ecdh.Create();
		var sessionId = Guid.NewGuid();
		var nonce = RandomNumberGenerator.GetBytes(16);

		var hello = new PairingHello(
			PeerId: _me.PeerId,
			DeviceName: _me.DeviceName,
			SessionId: sessionId,
			EcdhPublicKey: Ecdh.ExportPublic(ecdh),
			Nonce: nonce,
			ProtocolVersion: 1
		);

		var client = new TcpClient(AddressFamily.InterNetwork);
		await client.ConnectAsync(peerAddress, peerPort, ct);
		var stream = client.GetStream();

		await Framing.WriteFrameAsync(stream, PairingCodec.Encode(hello), ct);

		var remoteHelloBytes = await Framing.ReadFrameAsync(stream, ct);
		var remoteHello = PairingCodec.Decode<PairingHello>(remoteHelloBytes)
						 ?? throw new InvalidOperationException("Invalid remote hello.");

		// shared secret
		using var remotePub = Ecdh.ImportPublic(remoteHello.EcdhPublicKey);
		var shared = ecdh.DeriveKeyMaterial(remotePub);

		// transcript = stable concat
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

	public async Task SendDecisionAsync(bool accepted, CancellationToken ct)
		=> await Framing.WriteFrameAsync(Stream, PairingCodec.Encode(new PairingDecision(SessionId, accepted)), ct);

	public async Task<PairingDecision> WaitDecisionAsync(CancellationToken ct)
	{
		var bytes = await Framing.ReadFrameAsync(Stream, ct);
		return PairingCodec.Decode<PairingDecision>(bytes) ?? throw new InvalidOperationException("Invalid decision.");
	}

	public void Close() { try { Client.Close(); } catch { } }
}
