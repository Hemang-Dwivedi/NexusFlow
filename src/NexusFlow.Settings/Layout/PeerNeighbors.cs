namespace NexusFlow.Settings.Layout;

public sealed class PeerNeighbors
{
	// peerId -> neighbors
	public Dictionary<string, NeighborSet> Map { get; set; } = new();
}

public sealed class NeighborSet
{
	public string? Left { get; set; }
	public string? Right { get; set; }
	public string? Up { get; set; }
	public string? Down { get; set; }
}
