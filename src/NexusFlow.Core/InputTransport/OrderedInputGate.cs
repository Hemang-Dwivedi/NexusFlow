using System.Collections.Generic;

namespace NexusFlow.Core.InputTransport;

public sealed class OrderedInputGate
{
	private long _next = 1;
	private readonly SortedDictionary<long, NexusFlow.Protocol.Input.InputEventV1> _buf = new();
	private readonly int _maxBuffer;

	public OrderedInputGate(int maxBuffer = 256) => _maxBuffer = maxBuffer;

	public IEnumerable<NexusFlow.Protocol.Input.InputEventV1> Offer(NexusFlow.Protocol.Input.InputEventV1 ev)
	{
		// drop duplicates / old
		if (ev.Seq < _next) yield break;

		if (ev.Seq == _next)
		{
			yield return ev;
			_next++;

			// drain contiguous buffered
			while (_buf.Remove(_next, out var nextEv))
			{
				yield return nextEv;
				_next++;
			}
			yield break;
		}

		// gap: buffer
		if (_buf.Count >= _maxBuffer)
		{
			// drop newest (or oldest) – safest is drop newest to avoid blowing memory
			yield break;
		}

		_buf[ev.Seq] = ev;
	}

	public long ExpectedNextSeq => _next;
	public int BufferedCount => _buf.Count;
}