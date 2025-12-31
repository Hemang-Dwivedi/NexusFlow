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
