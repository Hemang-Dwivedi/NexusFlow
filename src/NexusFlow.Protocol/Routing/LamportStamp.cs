namespace NexusFlow.Protocol.Routing;

public readonly record struct LamportStamp(ulong Counter, Guid AuthorPeerId)
{
	public static bool IsNewer(LamportStamp a, LamportStamp b)
	{
		if (a.Counter != b.Counter) return a.Counter > b.Counter;
		// deterministic tie-break: compare Guid bytes
		Span<byte> ab = stackalloc byte[16];
		Span<byte> bb = stackalloc byte[16];
		a.AuthorPeerId.TryWriteBytes(ab);
		b.AuthorPeerId.TryWriteBytes(bb);
		return ab.SequenceCompareTo(bb) > 0;
	}
}
