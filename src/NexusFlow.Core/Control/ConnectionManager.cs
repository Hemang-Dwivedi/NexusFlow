using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using NexusFlow.Identity;
using NexusFlow.Protocol.Control;
using NexusFlow.Protocol.Transport;
using NexusFlow.Transport;
using NexusFlow.Trust;

namespace NexusFlow.Core.Control;

public sealed class ConnectionManager : IDisposable
{
	private readonly ILocalIdentity _me;
	private readonly TrustStore _trustStore;

	private readonly ConcurrentDictionary<string, ConnectedPeer> _connected = new();
	public event Action<ConnectedPeer>? PeerConnected;
	public event Action<string /*peerId*/>? PeerDisconnected;
	public event Action<string /*peerId*/, int /*rttMs*/>? PeerRttUpdated;

	public ConnectionManager(ILocalIdentity me, TrustStore trustStore)
	{
		_me = me;
		_trustStore = trustStore;
	}

	public IReadOnlyCollection<ConnectedPeer> Snapshot() => _connected.Values.ToList();

	// OUTGOING: connect + authenticate
	public async Task ConnectAsync(IPAddress address, int port, CancellationToken ct)
	{
		var client = new TcpClient(AddressFamily.InterNetwork);
		await client.ConnectAsync(address, port, ct);
		var stream = client.GetStream();

		var sessionId = Guid.NewGuid();
		var nonceA = RandomNumberGenerator.GetBytes(16);

		var hello = new ControlHello(_me.PeerId, _me.DeviceName, sessionId, nonceA, ProtocolVersion: 1);
		await FramingV2.WriteAsync(stream, MessageType.Control, ControlCodec.Encode(hello), ct);

		// Expect remote hello
		var (t1, p1) = await FramingV2.ReadAsync(stream, ct);
		if (t1 != MessageType.Control) throw new InvalidOperationException("Unexpected message type.");

		var remoteHello = ControlCodec.Decode<ControlHello>(p1) ?? throw new InvalidOperationException("Bad remote hello.");
		if (remoteHello.SessionId != sessionId) throw new InvalidOperationException("Session mismatch.");

		// Trust check
		var trusted = FindTrusted(remoteHello.PeerId);
		if (trusted is null)
		{
			client.Close();
			throw new UnauthorizedAccessException("Peer not trusted.");
		}

		var key = TrustKeys.KeyFromFingerprintHex(trusted.Fingerprint);
		var transcript = BuildTranscript(hello, remoteHello);
		var mac = TrustKeys.ComputeMac(key, transcript);

		await FramingV2.WriteAsync(stream, MessageType.Control, ControlCodec.Encode(new ControlAuth(sessionId, _me.PeerId, mac)), ct);

		// Wait result
		var (t2, p2) = await FramingV2.ReadAsync(stream, ct);
		if (t2 != MessageType.Control) throw new InvalidOperationException("Unexpected message type.");
		var result = ControlCodec.Decode<ControlResult>(p2) ?? throw new InvalidOperationException("Bad result.");
		if (!result.Accepted)
		{
			client.Close();
			throw new UnauthorizedAccessException(result.Reason ?? "Rejected");
		}

		// Connected
		var cp = new ConnectedPeer(remoteHello.PeerId, remoteHello.DeviceName, address, client, stream);
		_connected[cp.PeerId] = cp;
		PeerConnected?.Invoke(cp);

		_ = Task.Run(() => HeartbeatLoopAsync(cp, ct), ct);
		_ = Task.Run(() => ReadLoopAsync(cp, ct), ct);
	}

	// INCOMING: called by TcpMuxHost with first payload already read (ControlHello)
	public async Task HandleIncomingAsync(TcpClient client, NetworkStream stream, byte[] firstPayload, CancellationToken ct)
	{
		var helloA = ControlCodec.Decode<ControlHello>(firstPayload) ?? throw new InvalidOperationException("Bad hello.");

		// Reject if not trusted
		var trusted = FindTrusted(helloA.PeerId);
		if (trusted is null)
		{
			await FramingV2.WriteAsync(stream, MessageType.Control, ControlCodec.Encode(new ControlResult(helloA.SessionId, false, "Not trusted")), ct);
			client.Close();
			return;
		}

		// Reply with our hello (same session id)
		var nonceB = RandomNumberGenerator.GetBytes(16);
		var helloB = new ControlHello(_me.PeerId, _me.DeviceName, helloA.SessionId, nonceB, ProtocolVersion: 1);
		await FramingV2.WriteAsync(stream, MessageType.Control, ControlCodec.Encode(helloB), ct);

		// Wait auth
		var (t2, p2) = await FramingV2.ReadAsync(stream, ct);
		if (t2 != MessageType.Control) throw new InvalidOperationException("Unexpected type.");
		var auth = ControlCodec.Decode<ControlAuth>(p2) ?? throw new InvalidOperationException("Bad auth.");
		if (auth.SessionId != helloA.SessionId) throw new InvalidOperationException("Session mismatch.");

		// Verify MAC
		var key = TrustKeys.KeyFromFingerprintHex(trusted.Fingerprint);
		var transcript = BuildTranscript(helloA, helloB);
		var expected = TrustKeys.ComputeMac(key, transcript);

		if (!CryptographicOperations.FixedTimeEquals(expected, auth.Mac))
		{
			await FramingV2.WriteAsync(stream, MessageType.Control, ControlCodec.Encode(new ControlResult(helloA.SessionId, false, "Auth failed")), ct);
			client.Close();
			return;
		}

		await FramingV2.WriteAsync(stream, MessageType.Control, ControlCodec.Encode(new ControlResult(helloA.SessionId, true, null)), ct);

		var remoteAddr = ((IPEndPoint)client.Client.RemoteEndPoint!).Address;
		var cp = new ConnectedPeer(helloA.PeerId, helloA.DeviceName, remoteAddr, client, stream);
		_connected[cp.PeerId] = cp;
		PeerConnected?.Invoke(cp);

		_ = Task.Run(() => HeartbeatLoopAsync(cp, ct), ct);
		_ = Task.Run(() => ReadLoopAsync(cp, ct), ct);
	}

	private async Task ReadLoopAsync(ConnectedPeer peer, CancellationToken ct)
	{
		try
		{
			while (!ct.IsCancellationRequested)
			{
				var (type, payload) = await FramingV2.ReadAsync(peer.Stream, ct);
				if (type != MessageType.Control) continue;

				// Ping/Pong messages
				var msgType = ControlCodec.PeekType(payload);
				if (msgType == nameof(Ping))
				{
					var ping = ControlCodec.Decode<Ping>(payload)!;
					await FramingV2.WriteAsync(peer.Stream, MessageType.Control, ControlCodec.Encode(new Pong(ping.TicksUtc)), ct);
				}
				else if (msgType == nameof(Pong))
				{
					var pong = ControlCodec.Decode<Pong>(payload)!;
					var sent = new DateTime(pong.TicksUtc, DateTimeKind.Utc);
					var rtt = (int)Math.Max(0, (DateTime.UtcNow - sent).TotalMilliseconds);
					PeerRttUpdated?.Invoke(peer.PeerId, rtt);
				}
			}
		}
		catch { /* disconnect */ }
		finally
		{
			Disconnect(peer.PeerId);
		}
	}

	private async Task HeartbeatLoopAsync(ConnectedPeer peer, CancellationToken ct)
	{
		try
		{
			while (!ct.IsCancellationRequested)
			{
				await Task.Delay(1000, ct);
				var nowTicks = DateTime.UtcNow.Ticks;
				await FramingV2.WriteAsync(peer.Stream, MessageType.Control, ControlCodec.Encode(new Ping(nowTicks)), ct);
			}
		}
		catch { /* disconnect */ }
	}

	public void Disconnect(string peerId)
	{
		if (_connected.TryRemove(peerId, out var p))
		{
			try { p.Client.Close(); } catch { }
			PeerDisconnected?.Invoke(peerId);
		}
	}

	private TrustedPeer? FindTrusted(string peerId)
		=> _trustStore.Load().Peers.FirstOrDefault(p => p.PeerId == peerId);

	private static byte[] BuildTranscript(ControlHello a, ControlHello b)
	{
		// deterministic ordering by PeerId
		var first = string.CompareOrdinal(a.PeerId, b.PeerId) <= 0 ? a : b;
		var second = ReferenceEquals(first, a) ? b : a;

		using var ms = new MemoryStream();
		void W(string s) { var bytes = Encoding.UTF8.GetBytes(s); ms.Write(bytes); ms.WriteByte(0); }
		void WB(byte[] x) { ms.Write(x); ms.WriteByte(0); }

		W(first.PeerId); W(first.DeviceName); W(first.SessionId.ToString()); WB(first.Nonce);
		W(second.PeerId); W(second.DeviceName); W(second.SessionId.ToString()); WB(second.Nonce);

		return ms.ToArray();
	}

	public void Dispose()
	{
		foreach (var id in _connected.Keys.ToList())
			Disconnect(id);
	}
}

public sealed class ConnectedPeer
{
	public string PeerId { get; }
	public string DeviceName { get; }
	public IPAddress Address { get; }
	internal TcpClient Client { get; }
	internal NetworkStream Stream { get; }

	internal ConnectedPeer(string peerId, string deviceName, IPAddress address, TcpClient client, NetworkStream stream)
	{
		PeerId = peerId;
		DeviceName = deviceName;
		Address = address;
		Client = client;
		Stream = stream;
	}
}
