namespace NexusFlow.Display.Models;

public sealed record PeerDisplayCluster(
	string PeerId,
	string PeerName,
	IReadOnlyList<DisplaySnapshot> Displays
);
