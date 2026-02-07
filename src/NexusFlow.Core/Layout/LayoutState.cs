namespace NexusFlow.Core.Layout;

public sealed class LayoutState : ILayoutState
{
	private readonly object _gate = new();

	public LayoutSnapshot? Current { get; private set; }
	public event Action<LayoutSnapshot?>? Changed;

	public void Set(LayoutSnapshot? snapshot)
	{
		lock (_gate)
		{
			Current = snapshot;
		}
		Changed?.Invoke(Current);
	}

	public void UpsertPeerRect(PeerRect peer)
	{
		LayoutSnapshot next;
		lock (_gate)
		{
			var cur = Current;
			var peers = (cur?.Peers?.ToList() ?? new List<PeerRect>());

			var idx = peers.FindIndex(p => p.PeerId == peer.PeerId);
			if (idx >= 0) peers[idx] = peer;
			else peers.Add(peer);

			next = new LayoutSnapshot(peers);
			Current = next;
		}
		Changed?.Invoke(next);
	}

	public void RemovePeer(string peerId)
	{
		LayoutSnapshot? next = null;

		lock (_gate)
		{
			if (Current is null) return;

			var peers = Current.Peers.ToList();
			var removed = peers.RemoveAll(p => p.PeerId == peerId);
			if (removed == 0) return;

			next = new LayoutSnapshot(peers);
			Current = next;
		}

		Changed?.Invoke(next);
	}
}
