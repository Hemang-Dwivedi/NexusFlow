namespace NexusFlow.Settings.Layout;

public sealed class LayoutState
{
	public int Version { get; set; } = 1;

	// Keyed by PeerId (for Phase 1 local only, still keep it keyed)
	public Dictionary<string, PeerLayoutState> Peers { get; set; } = new();
}

public sealed class PeerLayoutState
{
	public double AppliedOffsetX { get; set; }
	public double AppliedOffsetY { get; set; }

	// Optional: snapshot to help with future matching
	public List<string> DisplayStableIds { get; set; } = new();
}
