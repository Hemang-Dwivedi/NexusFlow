using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using NexusFlow.Identity;
using NexusFlow.Protocol.Pairing;
using NexusFlow.Transport;
using NexusFlow.Trust;

namespace NexusFlow.Core.Trust;

public sealed class PairingListener : IDisposable
{
	private readonly ILocalIdentity _me;
	private readonly int _port;

	private TcpListener? _listener;
	private CancellationTokenSource? _cts;
	private Task? _loop;

	public event Action<IncomingPairingSession>? IncomingPairing;

	public PairingListener(ILocalIdentity me, int port)
	{
		_me = me;
		_port = port;
	}

	public void Start()
	{
		if (_cts is not null) return;

		_cts = new CancellationTokenSource();
		_listener = new TcpListener(IPAddress.Any, _port);
		_listener.Start();

		_loop = AcceptLoopAsync(_cts.Token);
	}

	public async Task StopAsync()
	{
		if (_cts is null) return;

		_cts.Cancel();
		try { _listener?.Stop(); } catch { }
		try { if (_loop is not null) await _loop; } catch { }

		_cts.Dispose();
		_cts = null;
		_listener = null;
		_loop = null;
	}

	private async Task AcceptLoopAsync(CancellationToken ct)
	{
		while (!ct.IsCancellationRequested)
		{
			TcpClient client;
			try
			{
				client = await _listener!.AcceptTcpClientAsync(ct);
			}
			catch (OperationCanceledException) { break; }
			catch { continue; }

			_ = Task.Run(() => HandleClientAsync(client, ct), ct);
		}
	}

	private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
	{
		using var _ = client;
		using var ecdh = Ecdh.Create();
		var nonce = RandomNumberGenerator.GetBytes(16);

		NetworkStream stream;
		try { stream = client.GetStream(); }
		catch { return; }

		// 1) Receive initiator hello
		PairingHello initiatorHello;
		try
		{
			var bytes = await Framing.ReadFrameAsync(stream, ct);
			initiatorHello = PairingCodec.Decode<PairingHello>(bytes) ?? throw new InvalidOperationException();
		}
		catch { return; }

		// 2) Respond with our hello (same session id)
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
			await Framing.WriteFrameAsync(stream, PairingCodec.Encode(myHello), ct);
		}
		catch { return; }

		// 3) Compute shared + transcript + code
		byte[] shared;
		string code;
		string fingerprint;

		try
		{
			using var initiatorPub = Ecdh.ImportPublic(initiatorHello.EcdhPublicKey);
			shared = ecdh.DeriveKeyMaterial(initiatorPub);

			var transcript = PairingCoordinator.BuildTranscriptStatic(myHello, initiatorHello);
			code = Sas.Compute6DigitCode(shared, transcript);
			fingerprint = Sas.Fingerprint(shared, transcript);
		}
		catch { return; }

		// 4) Raise incoming session to UI via event (UI will accept/reject)
		IncomingPairing?.Invoke(new IncomingPairingSession(
			initiatorHello.SessionId,
			initiatorHello.PeerId,
			initiatorHello.DeviceName,
			code,
			fingerprint,
			stream
		));

		// Note: stream stays open; IncomingPairingSession controls decision exchange.
		// We don't dispose stream here.
		await Task.CompletedTask;
	}

	public void Dispose() => _ = StopAsync();
}

public sealed class IncomingPairingSession
{
	public Guid SessionId { get; }
	public string RemotePeerId { get; }
	public string RemoteDeviceName { get; }
	public string Code6Digits { get; }
	public string Fingerprint { get; }

	private readonly NetworkStream _stream;

	internal IncomingPairingSession(Guid id, string remotePeerId, string remoteName, string code, string fingerprint, NetworkStream stream)
	{
		SessionId = id;
		RemotePeerId = remotePeerId;
		RemoteDeviceName = remoteName;
		Code6Digits = code;
		Fingerprint = fingerprint;
		_stream = stream;
	}

	public Task SendDecisionAsync(bool accepted, CancellationToken ct)
		=> Framing.WriteFrameAsync(_stream, PairingCodec.Encode(new PairingDecision(SessionId, accepted)), ct);

	public async Task<PairingDecision> WaitDecisionAsync(CancellationToken ct)
	{
		var bytes = await Framing.ReadFrameAsync(_stream, ct);
		return PairingCodec.Decode<PairingDecision>(bytes) ?? throw new InvalidOperationException("Invalid decision.");
	}
}
