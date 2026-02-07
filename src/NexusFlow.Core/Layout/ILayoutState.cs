namespace NexusFlow.Core.Layout;

public interface ILayoutState
{
	LayoutSnapshot? Current { get; }
	event Action<LayoutSnapshot?> Changed;

	void Set(LayoutSnapshot? snapshot);

	// NEW: allow incremental peer updates
	void UpsertPeerRect(PeerRect peer);
	void RemovePeer(string peerId);
}
