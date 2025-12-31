using Microsoft.Extensions.Hosting;
using NexusFlow.Core.Control;
using NexusFlow.Core.Diagnostics;
using NexusFlow.Core.Routing;
using NexusFlow.Protocol.Control;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace NexusFlow.App.Services;

public sealed class RoutingWireupHostedService : IHostedService
{
	private const string Cat = "routing.net";

	private readonly ConnectionManager _cm;
	private readonly IRoutingEngine _routing;
	private readonly IDiagnosticsLog _log;

	public RoutingWireupHostedService(ConnectionManager cm, IRoutingEngine routing, IDiagnosticsLog log)
	{
		_cm = cm;
		_routing = routing;
		_log = log;
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

		object? msg = null;
		if (typeName == nameof(SetActiveTargetV2)) msg = ControlCodec.Decode<SetActiveTargetV2>(payload);
		else if (typeName == nameof(SetActiveSourceV2)) msg = ControlCodec.Decode<SetActiveSourceV2>(payload);
		else if (typeName == nameof(RoutingStateSyncV2)) msg = ControlCodec.Decode<RoutingStateSyncV2>(payload);
		else return;

		if (msg is null) return;

		_log.Trace(Cat, $"RX {typeName} from={fromPeerId}");

		var res = _routing.TryApplyRemoteV2(msg);

		if (res.Decision == RoutingApplyDecision.Applied)
			_log.Info(Cat, $"APPLIED {typeName} from={fromPeerId}");
		else
			_log.Warn(Cat, $"IGNORED {typeName} from={fromPeerId} reason={res.Decision} {res.Detail}");
	}

	private async void OnPeerConnected(ConnectedPeer peer)
	{
		try
		{
			var snap = _routing.GetSnapshotV2();
			_log.Info(Cat, $"TX RoutingStateSyncV2 -> {peer.PeerId}");

			await _cm.SendToPeerAsync(
				peer.PeerId,
				new RoutingStateSyncV2(
					snap.ActiveTargetPeerId, snap.TargetStamp,
					snap.ActiveSourcePeerId, snap.SourceStamp
				)
			).ConfigureAwait(false);
		}
		catch (Exception ex)
		{
			_log.Warn(Cat, $"Failed to send RoutingStateSyncV2 to {peer.PeerId}: {ex.Message}");
		}
	}

	private async void OnPeerDisconnected(string peerId)
	{
		try
		{
			_log.Warn(Cat, $"peer disconnected: {peerId}");
			await _routing.HandlePeerDisconnectedAsync(peerId).ConfigureAwait(false);
		}
		catch (Exception ex)
		{
			_log.Warn(Cat, $"HandlePeerDisconnected failed: {ex.Message}");
		}
	}
}
