using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using NexusFlow.Core.Routing;
using NexusFlow.Identity;
using NexusFlow.Protocol.Control;
using NexusFlow.Protocol.Transport;
using NexusFlow.Transport;
using NexusFlow.Trust;

namespace NexusFlow.Core.Control;

/// <summary>
/// Authenticated, trust-gated control-channel connection manager.
/// Owns TCP sessions and exposes:
/// - peer connect/disconnect/RTT
/// - control-message dispatch
/// - broadcast / send helpers
/// </summary>
public sealed class ConnectionManager : IControlBroadcaster, IDisposable
{
	private readonly ILocalIdentity _me;
	private readonly TrustStore _trustStore;

	private readonly ConcurrentDictionary<string, ConnectedPeer> _connected = new();

	public event Action<ConnectedPeer>? PeerConnected;
	public event Action<string>? PeerDisconnected;
	public event Action<string, int>? PeerRttUpdated;
	public event Action<string, object>? ControlMessageReceived;


	public ConnectionManager(ILocalIdentity me, TrustStore trustStore)
	{
		_me = me;
		_trustStore = trustStore;
	}

	public IReadOnlyCollection<ConnectedPeer> Snapshot() => _connected.Values.ToList();

	public bool IsConnected(string peerId) => _connected.ContainsKey(peerId);

	// ------------------------------------------------------------------
	// IControlBroadcaster
	// ------------------------------------------------------------------

	public async Task SendToPeerAsync(string peerId, object controlMessage, CancellationToken ct = default)
	{
		if (!_connected.TryGetValue(peerId, out var peer))
			throw new InvalidOperationException($"Peer not connected: {peerId}");

		var payload = ControlCodec.Encode(controlMessage);
		await peer.SendControlFrameAsync(payload, ct).ConfigureAwait(false);
	}

	public async Task BroadcastAsync(object controlMessage, CancellationToken ct = default)
	{
		var payload = ControlCodec.Encode(controlMessage);

		foreach (var peer in _connected.Values)
		{
			_ = SendBestEffortAsync(peer, payload, ct);
		}

		await Task.CompletedTask;
	}

	private static async Task SendBestEffortAsync(ConnectedPeer peer, byte[] payload, CancellationToken ct)
	{
		try
		{
			await peer.SendControlFrameAsync(payload, ct).ConfigureAwait(false);
		}
		catch { }
	}

	// ------------------------------------------------------------------
	// OUTGOING
	// ------------------------------------------------------------------

	public async Task ConnectAsync(IPAddress address, int port, CancellationToken ct)
	{
		var client = new TcpClient(AddressFamily.InterNetwork);
		await client.ConnectAsync(address, port, ct);
		var stream = client.GetStream();

		var sessionId = Guid.NewGuid();
		var nonceA = RandomNumberGenerator.GetBytes(16);

		var hello = new ControlHello(_me.PeerId, _me.DeviceName, sessionId, nonceA, 1);
		await FramingV2.WriteAsync(stream, MessageType.Control, ControlCodec.Encode(hello), ct);

		var (_, payload) = await FramingV2.ReadAsync(stream, ct);
		var remoteHello = ControlCodec.Decode<ControlHello>(payload)!;

		var trusted = FindTrusted(remoteHello.PeerId)
			?? throw new UnauthorizedAccessException("Peer not trusted");

		var key = TrustKeys.KeyFromFingerprintHex(trusted.Fingerprint);
		var mac = TrustKeys.ComputeMac(key, BuildTranscript(hello, remoteHello));

		await FramingV2.WriteAsync(
			stream,
			MessageType.Control,
			ControlCodec.Encode(new ControlAuth(sessionId, _me.PeerId, mac)),
			ct
		);

		var (_, resultPayload) = await FramingV2.ReadAsync(stream, ct);
		var result = ControlCodec.Decode<ControlResult>(resultPayload)!;
		if (!result.Accepted)
			throw new UnauthorizedAccessException(result.Reason);

		AddPeer(new ConnectedPeer(remoteHello.PeerId, remoteHello.DeviceName, address, client, stream));
		await SendToPeerAsync(remoteHello.PeerId, new RoutingStateSync(remoteHello.PeerId, _me.PeerId), ct);

		StartLoops(remoteHello.PeerId, ct);
	}

	// ------------------------------------------------------------------
	// INCOMING
	// ------------------------------------------------------------------

	public async Task HandleIncomingAsync(TcpClient client, NetworkStream stream, byte[] firstPayload, CancellationToken ct)
	{
		var helloA = ControlCodec.Decode<ControlHello>(firstPayload)!;

		var trusted = FindTrusted(helloA.PeerId);
		if (trusted is null)
		{
			await FramingV2.WriteAsync(
				stream,
				MessageType.Control,
				ControlCodec.Encode(new ControlResult(helloA.SessionId, false, "Not trusted")),
				ct
			);
			client.Close();
			return;
		}

		var nonceB = RandomNumberGenerator.GetBytes(16);
		var helloB = new ControlHello(_me.PeerId, _me.DeviceName, helloA.SessionId, nonceB, 1);

		await FramingV2.WriteAsync(stream, MessageType.Control, ControlCodec.Encode(helloB), ct);

		var (_, authPayload) = await FramingV2.ReadAsync(stream, ct);
		var auth = ControlCodec.Decode<ControlAuth>(authPayload)!;

		var key = TrustKeys.KeyFromFingerprintHex(trusted.Fingerprint);
		var expected = TrustKeys.ComputeMac(key, BuildTranscript(helloA, helloB));

		if (!CryptographicOperations.FixedTimeEquals(expected, auth.Mac))
		{
			await FramingV2.WriteAsync(
				stream,
				MessageType.Control,
				ControlCodec.Encode(new ControlResult(helloA.SessionId, false, "Auth failed")),
				ct
			);
			client.Close();
			return;
		}

		await FramingV2.WriteAsync(
			stream,
			MessageType.Control,
			ControlCodec.Encode(new ControlResult(helloA.SessionId, true, null)),
			ct
		);

		var addr = ((IPEndPoint)client.Client.RemoteEndPoint!).Address;
		AddPeer(new ConnectedPeer(helloA.PeerId, helloA.DeviceName, addr, client, stream));
		StartLoops(helloA.PeerId, ct);
	}

	private void AddPeer(ConnectedPeer peer)
	{
		if (_connected.TryRemove(peer.PeerId, out var old))
			try { old.Client.Close(); } catch { }

		_connected[peer.PeerId] = peer;
		PeerConnected?.Invoke(peer);
	}

	private void StartLoops(string peerId, CancellationToken ct)
	{
		var peer = _connected[peerId];
		_ = Task.Run(() => ReadLoopAsync(peer, ct), ct);
		_ = Task.Run(() => HeartbeatLoopAsync(peer, ct), ct);
	}

	private async Task ReadLoopAsync(ConnectedPeer peer, CancellationToken ct)
	{
		try
		{
			while (!ct.IsCancellationRequested)
			{
				var (_, payload) = await FramingV2.ReadAsync(peer.Stream, ct);
				var type = ControlCodec.PeekType(payload);

				if (type == nameof(Ping))
				{
					var ping = ControlCodec.Decode<Ping>(payload)!;
					await peer.SendControlFrameAsync(
						ControlCodec.Encode(new Pong(ping.TicksUtc)), ct);
				}
				else if (type == nameof(Pong))
				{
					var pong = ControlCodec.Decode<Pong>(payload)!;
					var rtt = (int)(DateTime.UtcNow -
						new DateTime(pong.TicksUtc, DateTimeKind.Utc)).TotalMilliseconds;
					PeerRttUpdated?.Invoke(peer.PeerId, rtt);
				}
				else
				{
					var decoded = ControlCodec.Decode<object>(payload);
					if (decoded is not null)
					{
						ControlMessageReceived?.Invoke(peer.PeerId, payload);
					}

				}
			}
		}
		catch { }
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
				await peer.SendControlFrameAsync(
					ControlCodec.Encode(new Ping(DateTime.UtcNow.Ticks)), ct);
			}
		}
		catch { }
	}

	public void Disconnect(string peerId)
	{
		if (_connected.TryRemove(peerId, out var peer))
		{
			try { peer.Client.Close(); } catch { }
			PeerDisconnected?.Invoke(peerId);
		}
	}

	private TrustedPeer? FindTrusted(string peerId)
		=> _trustStore.Load().Peers.FirstOrDefault(p => p.PeerId == peerId);

	private static byte[] BuildTranscript(ControlHello a, ControlHello b)
	{
		var first = string.CompareOrdinal(a.PeerId, b.PeerId) <= 0 ? a : b;
		var second = ReferenceEquals(first, a) ? b : a;

		using var ms = new MemoryStream();
		void W(string s) { var b = Encoding.UTF8.GetBytes(s); ms.Write(b); ms.WriteByte(0); }
		void WB(byte[] b) { ms.Write(b); ms.WriteByte(0); }

		W(first.PeerId); W(first.DeviceName); W(first.SessionId.ToString()); WB(first.Nonce);
		W(second.PeerId); W(second.DeviceName); W(second.SessionId.ToString()); WB(second.Nonce);

		return ms.ToArray();
	}

	public void Dispose()
	{
		foreach (var id in _connected.Keys.ToList())
			Disconnect(id);
	}

	public async Task SendRoutingStateSyncAsync(string peerId, IRoutingEngine routing, CancellationToken ct)
	{
		var (t, s) = routing.GetSnapshot();
		await SendToPeerAsync(peerId, new RoutingStateSync(t, s), ct).ConfigureAwait(false);
	}

}

public sealed class ConnectedPeer
{
	public string PeerId { get; }
	public string DeviceName { get; }
	public IPAddress Address { get; }

	internal TcpClient Client { get; }
	internal NetworkStream Stream { get; }

	private readonly SemaphoreSlim _sendLock = new(1, 1);

	internal ConnectedPeer(string peerId, string deviceName, IPAddress address,
						   TcpClient client, NetworkStream stream)
	{
		PeerId = peerId;
		DeviceName = deviceName;
		Address = address;
		Client = client;
		Stream = stream;
	}

	internal async Task SendControlFrameAsync(byte[] payload, CancellationToken ct)
	{
		await _sendLock.WaitAsync(ct);
		try
		{
			await FramingV2.WriteAsync(Stream, MessageType.Control, payload, ct);
		}
		finally
		{
			_sendLock.Release();
		}
	}
}
