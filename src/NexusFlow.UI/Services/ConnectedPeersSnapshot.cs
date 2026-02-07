using System.Collections.ObjectModel;
using NexusFlow.Core.Control;

namespace NexusFlow.UI.Services;

public interface IConnectedPeersSnapshot
{
	string? LocalPeerId { get; }
	ObservableCollection<(string PeerId, string DisplayName)> ConnectedPeers { get; }
	IReadOnlyList<ConnectedPeer> Snapshot();
	event Action? Changed;
}

public sealed class ConnectedPeersSnapshot : IConnectedPeersSnapshot, IDisposable
{
	public string? LocalPeerId { get; }
	public ObservableCollection<(string PeerId, string DisplayName)> ConnectedPeers { get; } = new();
	private readonly ConnectionManager _connections;
	private readonly object _gate = new();

	private List<ConnectedPeer> _cache = new();

	public event Action? Changed;

	public ConnectedPeersSnapshot(ConnectionManager connections)
	{
		_connections = connections;

		// init cache
		_cache = _connections.Snapshot().ToList();
		Refresh();
		_connections.PeerConnected += OnPeerChanged;
		_connections.PeerDisconnected += _ => OnPeerChanged(null);
	}

	private void Refresh()
	{
		ConnectedPeers.Clear();

		ConnectedPeers.Add((LocalPeerId, "(This device)"));

		foreach (var p in _connections.Snapshot())
			ConnectedPeers.Add((p.PeerId, p.DeviceName));
	}

	private void OnPeerChanged(ConnectedPeer? _)
	{
		lock (_gate) _cache = _connections.Snapshot().ToList();
		Changed?.Invoke();
		Refresh();
	}

	public IReadOnlyList<ConnectedPeer> Snapshot()
	{
		lock (_gate) return _cache.ToList();
	}

	public void Dispose()
	{
		_connections.PeerConnected -= OnPeerChanged;
		_connections.PeerDisconnected -= _ => OnPeerChanged(null); // if you used lambda, store delegate instead
	}
}
