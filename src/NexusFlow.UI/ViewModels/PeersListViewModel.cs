using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using NexusFlow.App.ViewModels;
using NexusFlow.Core.Discovery;
using NexusFlow.Discovery.Peers;
using NexusFlow.Identity;
using System;
using System.Collections.ObjectModel;
using System.Linq;

namespace NexusFlow.UI.ViewModels;

public sealed partial class PeersListViewModel : ObservableObject, IDisposable
{
	private readonly DiscoveryCoordinator _discovery;
	private readonly ILocalIdentity _identity;

	public ObservableCollection<PeerRowViewModel> Peers { get; } = new();

	public PeersListViewModel(DiscoveryCoordinator discovery, ILocalIdentity identity)
	{
		_discovery = discovery;
		_identity = identity;

		// Initial snapshot
		RefreshFromSnapshot();

		// Live updates
		_discovery.Registry.OnPeerDiscovered += OnPeerDiscovered;
		_discovery.Registry.OnPeerUpdated += OnPeerUpdated;
		_discovery.Registry.OnPeerLost += OnPeerLost;
	}

	public bool HasPeers => Peers.Count > 0;

	private void RefreshFromSnapshot()
	{
		var snapshot = _discovery.Registry.Snapshot()
			.Where(p => p.PeerId != _identity.PeerId)
			.OrderBy(p => p.DeviceName)
			.ToList();

		Dispatcher.UIThread.Post(() =>
		{
			Peers.Clear();
			foreach (var p in snapshot)
				Peers.Add(new PeerRowViewModel(p.PeerId, p.DeviceName, p.TcpPort, p.LastSeen));

			OnPropertyChanged(nameof(HasPeers));
		});
	}

	private void OnPeerDiscovered(PeerDiscovered e)
	{
		if (e.Peer.PeerId == _identity.PeerId) return;

		Dispatcher.UIThread.Post(() =>
		{
			if (Peers.Any(x => x.PeerId == e.Peer.PeerId)) return;

			Peers.Add(new PeerRowViewModel(e.Peer.PeerId, e.Peer.DeviceName, e.Peer.TcpPort, e.Peer.LastSeen));
			OnPropertyChanged(nameof(HasPeers));
		});
	}

	private void OnPeerUpdated(PeerUpdated e)
	{
		if (e.Peer.PeerId == _identity.PeerId) return;

		Dispatcher.UIThread.Post(() =>
		{
			var row = Peers.FirstOrDefault(x => x.PeerId == e.Peer.PeerId);
			if (row is null)
			{
				Peers.Add(new PeerRowViewModel(e.Peer.PeerId, e.Peer.DeviceName, e.Peer.TcpPort, e.Peer.LastSeen));
				OnPropertyChanged(nameof(HasPeers));
				return;
			}

			row.DeviceName = e.Peer.DeviceName;
			row.TcpPort = e.Peer.TcpPort;
			row.LastSeen = e.Peer.LastSeen;
		});
	}

	private void OnPeerLost(PeerLost e)
	{
		Dispatcher.UIThread.Post(() =>
		{
			var row = Peers.FirstOrDefault(x => x.PeerId == e.PeerId);
			if (row is null) return;

			Peers.Remove(row);
			OnPropertyChanged(nameof(HasPeers));
		});
	}

	public void Dispose()
	{
		_discovery.Registry.OnPeerDiscovered -= OnPeerDiscovered;
		_discovery.Registry.OnPeerUpdated -= OnPeerUpdated;
		_discovery.Registry.OnPeerLost -= OnPeerLost;
	}
}
