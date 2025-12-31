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
		// Apply remote routing messages
		_cm.ControlMessageReceived += OnControlMessage;

		// On connect: push our current routing state to the peer (heals missed updates)
		_cm.PeerConnected += OnPeerConnected;

		return Task.CompletedTask;
	}

	public Task StopAsync(CancellationToken cancellationToken)
	{
		_cm.ControlMessageReceived -= OnControlMessage;
		_cm.PeerConnected -= OnPeerConnected;
		return Task.CompletedTask;
	}

	private void OnControlMessage(string peerId, object msg)
	{
		// Only accept routing-related messages
		if (msg is SetActiveTarget or SetActiveSource or RoutingStateSync)
			_routing.ApplyRemote(msg);
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
			// ignore; disconnect cleanup will happen elsewhere
		}
	}
}
