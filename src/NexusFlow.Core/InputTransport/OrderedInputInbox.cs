using System.Collections.Generic;
using NexusFlow.Protocol.Input;

namespace NexusFlow.Core.InputTransport;

/// <summary>
/// Enforces strict in-order delivery per (FromPeerId) using Seq.
/// Buffers out-of-order events up to a bounded window.
/// </summary>
public sealed class OrderedInputInbox
{
	private readonly long _maxBuffer; // how many future seqs we tolerate

	private long _expectedSeq = 1;
	private readonly Dictionary<long, InputEventV1> _buffer = new();

	public OrderedInputInbox(long startExpectedSeq = 1, long maxBuffer = 2048)
	{
		_expectedSeq = startExpectedSeq;
		_maxBuffer = maxBuffer;
	}

	public long ExpectedSeq => _expectedSeq;
	public int BufferedCount => _buffer.Count;

	// Stats
	public long AppliedCount { get; private set; }
	public long DroppedOldCount { get; private set; }
	public long BufferedCountTotal { get; private set; }
	public long DroppedOverflowCount { get; private set; }
	public long GapCount { get; private set; }

	/// <summary>
	/// Push one event. Calls apply() for any newly in-order events (maybe multiple).
	/// </summary>
	public void Push(InputEventV1 ev, Action<InputEventV1> apply)
	{
		// Old / duplicate
		if (ev.Seq < _expectedSeq)
		{
			DroppedOldCount++;
			return;
		}

		// In-order
		if (ev.Seq == _expectedSeq)
		{
			ApplyAndAdvance(ev, apply);
			DrainBuffered(apply);
			return;
		}

		// Too far in the future -> drop or reset policy
		if (ev.Seq - _expectedSeq > _maxBuffer)
		{
			// Conservative policy: drop far-future packets.
			// Alternative policy is "fast-forward expectedSeq", but that hides gaps.
			DroppedOverflowCount++;
			return;
		}

		// Buffer (if not already)
		if (_buffer.TryAdd(ev.Seq, ev))
		{
			BufferedCountTotal++;
			GapCount++; // we observed a gap at least once (approx metric)
		}
	}

	private void ApplyAndAdvance(InputEventV1 ev, Action<InputEventV1> apply)
	{
		apply(ev);
		AppliedCount++;
		_expectedSeq++;
	}

	private void DrainBuffered(Action<InputEventV1> apply)
	{
		while (_buffer.TryGetValue(_expectedSeq, out var next))
		{
			_buffer.Remove(_expectedSeq);
			ApplyAndAdvance(next, apply);
		}
	}
}
