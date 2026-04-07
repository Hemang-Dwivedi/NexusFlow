namespace NexusFlow.Core.Layout;

public sealed record PeerRect(string PeerId, double X, double Y, double Width, double Height, string DeviceName = "")
{
	public bool Contains(double px, double py)
		=> px >= X && px < X + Width && py >= Y && py < Y + Height;
}

public sealed class LayoutSnapshot
{
	private readonly List<PeerRect> _peers;

	public LayoutSnapshot(IEnumerable<PeerRect> peers) => _peers = peers.ToList();

	public bool TryGetPeerRect(string peerId, out PeerRect rect)
	{
		rect = _peers.FirstOrDefault(p => p.PeerId == peerId)!;
		return rect is not null;
	}

	public bool TryFindPeerAt(double x, double y, out string peerId)
	{
		foreach (var p in _peers)
		{
			if (p.Contains(x, y))
			{
				peerId = p.PeerId;
				return true;
			}
		}
		peerId = "";
		return false;
	}

	public IReadOnlyList<PeerRect> Peers => _peers;
}
