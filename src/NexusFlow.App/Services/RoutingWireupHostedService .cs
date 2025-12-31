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
		return Task.CompletedTask;
	}

	public Task StopAsync(CancellationToken cancellationToken)
	{
		_cm.ControlMessageReceived -= OnControlPayload;
		_cm.PeerConnected -= OnPeerConnected;
		return Task.CompletedTask;
	}

	private void OnControlPayload(string peerId, byte[] payload)
	{
		var typeName = ControlCodec.PeekType(payload);

		if (typeName == nameof(SetActiveTarget))
		{
			var msg = ControlCodec.Decode<SetActiveTarget>(payload);
			if (msg is not null) _routing.ApplyRemote(msg);
			return;
		}

		if (typeName == nameof(SetActiveSource))
		{
			var msg = ControlCodec.Decode<SetActiveSource>(payload);
			if (msg is not null) _routing.ApplyRemote(msg);
			return;
		}

		if (typeName == nameof(RoutingStateSync))
		{
			var msg = ControlCodec.Decode<RoutingStateSync>(payload);
			if (msg is not null) _routing.ApplyRemote(msg);
			return;
		}

		// ignore other control messages here (Ping/Pong are handled inside ConnectionManager)
	}

	private async void OnPeerConnected(ConnectedPeer peer)
	{
		try
		{
			var (t, s) = _routing.GetSnapshot();
			await _cm.SendToPeerAsync(peer.PeerId, new RoutingStateSync(t, s));
		}
		catch
		{
			// ignore; disconnect cleanup elsewhere
		}
	}
}
