using System.Collections.ObjectModel;
using NexusFlow.Core.Control;
using NexusFlow.Identity;
using NexusFlow.UI.ViewModels;

namespace NexusFlow.UI.Services;

public sealed class ConnectedPeersSnapshot : IConnectedPeersSnapshot
{
	private readonly ConnectionManager _connections;

	public string LocalPeerId { get; }

	public ObservableCollection<(string PeerId, string DisplayName)> ConnectedPeers { get; }
		= new();

	public ConnectedPeersSnapshot(ConnectionManager connections, ILocalIdentity identity)
	{
		_connections = connections;
		LocalPeerId = identity.PeerId;

		Refresh();

		_connections.PeerConnected += _ => Refresh();
		_connections.PeerDisconnected += _ => Refresh();
	}

	private void Refresh()
	{
		ConnectedPeers.Clear();

		ConnectedPeers.Add((LocalPeerId, "(This device)"));

		foreach (var p in _connections.Snapshot())
			ConnectedPeers.Add((p.PeerId, p.DeviceName));
	}
}
