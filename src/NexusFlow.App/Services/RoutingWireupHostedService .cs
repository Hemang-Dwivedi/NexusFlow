using Microsoft.Extensions.Hosting;
using NexusFlow.Core.Control;
using NexusFlow.Core.Routing;
using NexusFlow.Protocol.Control;
using System.Threading;
using System.Threading.Tasks;

namespace NexusFlow.App.Services;

public sealed class RoutingWireupHostedService : IHostedService
{
	private readonly ConnectionManager _cm;
	private readonly IRoutingEngine _routing;

	public RoutingWireupHostedService(ConnectionManager cm, IRoutingEngine routing)
	{
		_cm = cm;
		_routing = routing;
	}

	public Task StartAsync(CancellationToken cancellationToken)
	{
		_cm.ControlMessageReceived += OnControlPayload;
		_cm.PeerConnected += OnPeerConnected;
		_cm.PeerDisconnected += OnPeerDisconnected;
		return Task.CompletedTask;
	}

	public Task StopAsync(CancellationToken cancellationToken)
	{
		_cm.ControlMessageReceived -= OnControlPayload;
		_cm.PeerConnected -= OnPeerConnected;
		_cm.PeerDisconnected -= OnPeerDisconnected;
		return Task.CompletedTask;
	}

	private void OnControlPayload(string fromPeerId, byte[] payload)
	{
		var typeName = ControlCodec.PeekType(payload);

		if (typeName == nameof(SetActiveTargetV2))
		{
			var msg = ControlCodec.Decode<SetActiveTargetV2>(payload);
			if (msg is not null) _routing.ApplyRemoteV2(msg);
			return;
		}

		if (typeName == nameof(SetActiveSourceV2))
		{
			var msg = ControlCodec.Decode<SetActiveSourceV2>(payload);
			if (msg is not null) _routing.ApplyRemoteV2(msg);
			return;
		}

		if (typeName == nameof(RoutingStateSyncV2))
		{
			var msg = ControlCodec.Decode<RoutingStateSyncV2>(payload);
			if (msg is not null) _routing.ApplyRemoteV2(msg);
			return;
		}

		// ignore other control messages
	}

	private async void OnPeerConnected(ConnectedPeer peer)
	{
		try
		{
			var snap = _routing.GetSnapshotV2();
			await _cm.SendToPeerAsync(
				peer.PeerId,
				new RoutingStateSyncV2(
					snap.ActiveTargetPeerId, snap.TargetStamp,
					snap.ActiveSourcePeerId, snap.SourceStamp
				)
			).ConfigureAwait(false);
		}
		catch { }
	}

	private async void OnPeerDisconnected(string peerId)
	{
		try
		{
			await _routing.HandlePeerDisconnectedAsync(peerId).ConfigureAwait(false);
		}
		catch { }
	}
}
