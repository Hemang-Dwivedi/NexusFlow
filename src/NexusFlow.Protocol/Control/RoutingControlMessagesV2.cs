namespace NexusFlow.Protocol.Control;

/// <summary>
/// Lamport stamp for deterministic last-writer-wins (LWW).
/// Compare by Counter, then by PeerId ordinal.
/// </summary>
public readonly record struct LamportStamp(long Counter, string PeerId)
{
	public static int Compare(in LamportStamp a, in LamportStamp b)
	{
		var c = a.Counter.CompareTo(b.Counter);
		if (c != 0) return c;
		return string.CompareOrdinal(a.PeerId, b.PeerId);
	}

	public bool IsNewerThan(in LamportStamp other) => Compare(this, other) > 0;
}

public sealed record SetActiveTargetV2(string TargetPeerId, LamportStamp Stamp);

public sealed record SetActiveSourceV2(string SourcePeerId, LamportStamp Stamp);

public sealed record RoutingStateSyncV2(
	string ActiveTargetPeerId, LamportStamp TargetStamp,
	string ActiveSourcePeerId, LamportStamp SourceStamp
);

/// <summary>
/// Sent by peer A to peer B after A applies a layout position for B.
/// Tells B: "I placed you at (RelDx, RelDy) pixels right/down from my desktop origin."
/// B uses this to position A in its own virtual coordinate space.
/// </summary>
public sealed record LayoutPositionSyncV1(
    string ByPeerId,   // who applied the layout
    string ForPeerId,  // which peer was repositioned
    double RelDx,      // B's canvas X offset from A's leftmost virtual edge
    double RelDy       // B's canvas Y offset from A's topmost virtual edge
);
