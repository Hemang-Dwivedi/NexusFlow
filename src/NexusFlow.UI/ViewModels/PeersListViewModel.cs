using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NexusFlow.Core.Control;
using NexusFlow.Core.Discovery;
using NexusFlow.Core.Trust;
using NexusFlow.Discovery.Peers;
using NexusFlow.Identity;
using NexusFlow.Trust;
using NexusFlow.UI.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace NexusFlow.UI.ViewModels;

public sealed partial class PeersListViewModel : ObservableObject, IDisposable
{
	private readonly DiscoveryCoordinator _discovery;
	private readonly ILocalIdentity _identity;

	private readonly Dictionary<string, PeerRowViewModel> _peerIndex = new();
	private readonly HashSet<string> _trustedPeerIds = new();

	private readonly PairingCoordinator _pairing;
	private readonly PairingListener _pairingListener;
	private readonly IPairingDialogService _dialogs;
	private readonly TrustStore _trustStore;

	private readonly ConnectionManager _connections;

	public ObservableCollection<PeerRowViewModel> Peers { get; } = new();
	public bool HasPeers => Peers.Count > 0;

	public PeersListViewModel(
		DiscoveryCoordinator discovery,
		ILocalIdentity identity,
		PairingCoordinator pairing,
		PairingListener pairingListener,
		IPairingDialogService dialogs,
		TrustStore trustStore,
		ConnectionManager connections)
	{
		_discovery = discovery;
		_identity = identity;
		_pairing = pairing;
		_pairingListener = pairingListener;
		_dialogs = dialogs;
		_trustStore = trustStore;
		_connections = connections;

		Peers.CollectionChanged += (_, __) => OnPropertyChanged(nameof(HasPeers));

		LoadTrustCache();
		RefreshFromSnapshot();

		_discovery.Registry.OnPeerDiscovered += OnPeerDiscovered;
		_discovery.Registry.OnPeerUpdated += OnPeerUpdated;
		_discovery.Registry.OnPeerLost += OnPeerLost;

		// Incoming pairing requests
		_pairingListener.IncomingPairing += OnIncomingPairing;

		// Connection events (secure control channel)
		_connections.PeerConnected += OnPeerConnected;
		_connections.PeerDisconnected += OnPeerDisconnected;
		_connections.PeerRttUpdated += OnPeerRttUpdated;
	}

	private void LoadTrustCache()
	{
		_trustedPeerIds.Clear();
		var state = _trustStore.Load();
		foreach (var p in state.Peers)
			_trustedPeerIds.Add(p.PeerId);
	}

	private void AddOrUpdatePeer(DiscoveredPeer peer)
	{
		if (peer.LastKnownAddress is null) return;

		Dispatcher.UIThread.Post(() =>
		{
			if (_peerIndex.TryGetValue(peer.PeerId, out var existing))
			{
				existing.DeviceName = peer.DeviceName;
				existing.TcpPort = peer.TcpPort;
				existing.LastSeen = peer.LastSeen;
				existing.Address = peer.LastKnownAddress!;
				existing.IsTrusted = _trustedPeerIds.Contains(peer.PeerId);
				existing.IsConnected = _connections.Snapshot().Any(c => c.PeerId == peer.PeerId);
				return;
			}

			var row = new PeerRowViewModel(
				peer.PeerId,
				peer.DeviceName,
				peer.TcpPort,
				peer.LastSeen,
				peer.LastKnownAddress!,
				_trustedPeerIds.Contains(peer.PeerId)
			);

			row.IsConnected = _connections.Snapshot().Any(c => c.PeerId == peer.PeerId);
			row.RttMs = null;

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

	private void RefreshFromSnapshot()
	{
		var snapshot = _discovery.Registry.Snapshot()
			.Where(p => p.PeerId != _identity.PeerId)
			.OrderBy(p => p.DeviceName)
			.ToList();

		Dispatcher.UIThread.Post(() =>
		{
			_peerIndex.Clear();
			Peers.Clear();

			var connected = _connections.Snapshot().Select(c => c.PeerId).ToHashSet();

			foreach (var p in snapshot)
			{
				if (p.LastKnownAddress is null) continue;

				var row = new PeerRowViewModel(
					p.PeerId,
					p.DeviceName,
					p.TcpPort,
					p.LastSeen,
					p.LastKnownAddress!,
					_trustedPeerIds.Contains(p.PeerId)
				);

				row.IsConnected = connected.Contains(p.PeerId);
				row.RttMs = null;

				_peerIndex[p.PeerId] = row;
				Peers.Add(row);
			}
		});
	}

	#region Pairing

	[RelayCommand]
	private async Task PairAsync(PeerRowViewModel row)
	{
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
			_trustedPeerIds.Add(session.RemotePeerId);
			MarkRowTrusted(session.RemotePeerId, true);
		}

		session.Close();
	}

	private async void OnIncomingPairing(IncomingPairingSession s)
	{
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
				_trustedPeerIds.Add(s.RemotePeerId);
				MarkRowTrusted(s.RemotePeerId, true);
			}

			s.Close();
		});
	}

	private void MarkRowTrusted(string peerId, bool trusted)
	{
		Dispatcher.UIThread.Post(() =>
		{
			if (_peerIndex.TryGetValue(peerId, out var row))
				row.IsTrusted = trusted;
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

	#endregion

	#region Connections (Secure Control Channel)

	[RelayCommand]
	private async Task ConnectAsync(PeerRowViewModel row)
	{
		if (!row.IsTrusted) return;
		if (row.IsConnected) return;

		var ct = CancellationToken.None;
		await _connections.ConnectAsync(row.Address, row.TcpPort, ct);
	}

	[RelayCommand]
	private void Disconnect(PeerRowViewModel row)
	{
		if (!row.IsConnected) return;
		_connections.Disconnect(row.PeerId);
	}

	private void OnPeerConnected(ConnectedPeer p)
	{
		Dispatcher.UIThread.Post(() =>
		{
			if (_peerIndex.TryGetValue(p.PeerId, out var row))
			{
				row.IsConnected = true;
				row.RttMs = null;
			}
		});
	}

	private void OnPeerDisconnected(string peerId)
	{
		Dispatcher.UIThread.Post(() =>
		{
			if (_peerIndex.TryGetValue(peerId, out var row))
			{
				row.IsConnected = false;
				row.RttMs = null;
			}
		});
	}

	private void OnPeerRttUpdated(string peerId, int rttMs)
	{
		Dispatcher.UIThread.Post(() =>
		{
			if (_peerIndex.TryGetValue(peerId, out var row))
				row.RttMs = rttMs;
		});
	}

	#endregion

	public void Dispose()
	{
		_discovery.Registry.OnPeerDiscovered -= OnPeerDiscovered;
		_discovery.Registry.OnPeerUpdated -= OnPeerUpdated;
		_discovery.Registry.OnPeerLost -= OnPeerLost;

		_pairingListener.IncomingPairing -= OnIncomingPairing;

		_connections.PeerConnected -= OnPeerConnected;
		_connections.PeerDisconnected -= OnPeerDisconnected;
		_connections.PeerRttUpdated -= OnPeerRttUpdated;
	}
}
