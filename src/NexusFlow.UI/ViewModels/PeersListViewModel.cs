using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NexusFlow.Core.Discovery;
using NexusFlow.Core.Trust;
using NexusFlow.Discovery.Peers;
using NexusFlow.Identity;
using NexusFlow.Trust;
using NexusFlow.UI.Services;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using static System.Windows.Forms.VisualStyles.VisualStyleElement.Window;

namespace NexusFlow.UI.ViewModels;

public sealed partial class PeersListViewModel : ObservableObject, IDisposable
{
	private readonly DiscoveryCoordinator _discovery;
	private readonly ILocalIdentity _identity;
	private readonly Dictionary<string, PeerRowViewModel> _peerIndex = new();
	private readonly PairingCoordinator _pairing;
	private readonly PairingListener _pairingListener;
	private readonly IPairingDialogService _dialogs;
	private readonly TrustStore _trustStore;
	public ObservableCollection<PeerRowViewModel> Peers { get; } = new();


	public PeersListViewModel(
	   DiscoveryCoordinator discovery,
	   ILocalIdentity identity,
	   PairingCoordinator pairing,
	   PairingListener pairingListener,
	   IPairingDialogService dialogs,
	   TrustStore trustStore)
	{
		_discovery = discovery;
		_identity = identity;
		_pairing = pairing;
		_pairingListener = pairingListener;
		_dialogs = dialogs;
		_trustStore = trustStore;

		Peers.CollectionChanged += (_, __) => OnPropertyChanged(nameof(HasPeers));

		RefreshFromSnapshot();

		_discovery.Registry.OnPeerDiscovered += OnPeerDiscovered;
		_discovery.Registry.OnPeerUpdated += OnPeerUpdated;
		_discovery.Registry.OnPeerLost += OnPeerLost;

		// Incoming pairing
		_pairingListener.IncomingPairing += OnIncomingPairing;
	}

	public bool HasPeers => Peers.Count > 0;
	private void AddOrUpdatePeer(DiscoveredPeer peer)
	{
		Dispatcher.UIThread.Post(() =>
		{
			if (_peerIndex.TryGetValue(peer.PeerId, out var existing))
			{
				existing.DeviceName = peer.DeviceName;
				existing.TcpPort = peer.TcpPort;
				existing.LastSeen = peer.LastSeen;
				return;
			}

			var row = new PeerRowViewModel(
				peer.PeerId,
				peer.DeviceName,
				peer.TcpPort,
				peer.LastSeen,
				peer.LastKnownAddress
			);

			_peerIndex[peer.PeerId] = row;
			Peers.Add(row);

		});
	}

	private void OnPeerDiscovered(PeerDiscovered e)
	{
		if (e.Peer.PeerId == _identity.PeerId) return;
		AddOrUpdatePeer(e.Peer);
	}

	private void OnPeerUpdated(PeerUpdated e)
	{
		if (e.Peer.PeerId == _identity.PeerId) return;
		AddOrUpdatePeer(e.Peer);
	}

	private void OnPeerLost(PeerLost e)
	{
		Dispatcher.UIThread.Post(() =>
		{
			if (_peerIndex.Remove(e.PeerId, out var row))
				Peers.Remove(row);
		});
	}

	#region Pairing
	[RelayCommand]
	private async Task PairAsync(PeerRowViewModel row)
	{
		// Outgoing pairing flow
		var ct = CancellationToken.None;

		var session = await _pairing.BeginPairingAsync(row.Address, row.TcpPort, ct);

		var vm = new PairingDialogViewModel("Pair Device", session.RemoteDeviceName, session.Code6Digits);
		var acceptedLocal = await _dialogs.ShowCompareCodeAsync(vm, ct);

		await session.SendDecisionAsync(acceptedLocal, ct);

		var remoteDecision = await session.WaitDecisionAsync(ct);
		var acceptedRemote = remoteDecision.Accepted;

		if (acceptedLocal && acceptedRemote)
		{
			PersistTrust(session.RemotePeerId, session.RemoteDeviceName, session.Fingerprint);
		}

		session.Close();
	}

	private async void OnIncomingPairing(IncomingPairingSession s)
	{
		// Must show dialog on UI thread, but we can await in a fire-and-forget safe way
		await Dispatcher.UIThread.InvokeAsync(async () =>
		{
			var ct = CancellationToken.None;

			var vm = new PairingDialogViewModel("Incoming Pair Request", s.RemoteDeviceName, s.Code6Digits);
			var acceptedLocal = await _dialogs.ShowCompareCodeAsync(vm, ct);

			await s.SendDecisionAsync(acceptedLocal, ct);
			var remoteDecision = await s.WaitDecisionAsync(ct);

			if (acceptedLocal && remoteDecision.Accepted)
			{
				PersistTrust(s.RemotePeerId, s.RemoteDeviceName, s.Fingerprint);
			}
		});
	}

	private void PersistTrust(string peerId, string deviceName, string fingerprint)
	{
		var state = _trustStore.Load();

		state.Peers.RemoveAll(p => p.PeerId == peerId);

		state.Peers.Add(new TrustedPeer(
			PeerId: peerId,
			DeviceName: deviceName,
			Fingerprint: fingerprint,
			TrustedAtUtc: DateTimeOffset.UtcNow));

		_trustStore.Save(state);
	}

	// refresh + event handlers: ensure you pass Address into PeerRowViewModel
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
			{
				if (p.LastKnownAddress is null) continue;
				Peers.Add(new PeerRowViewModel(p.PeerId, p.DeviceName, p.TcpPort, p.LastSeen, p.LastKnownAddress));
			}
		});
	}

	// TODO: update OnPeerDiscovered/Updated/Lost similarly with address.
	// Keep your existing logic but always pass p.LastKnownAddress.
	// ...

	public void Dispose()
	{
		_discovery.Registry.OnPeerDiscovered -= OnPeerDiscovered;
		_discovery.Registry.OnPeerUpdated -= OnPeerUpdated;
		_discovery.Registry.OnPeerLost -= OnPeerLost;
		_pairingListener.IncomingPairing -= OnIncomingPairing;
	}
	#endregion
}
