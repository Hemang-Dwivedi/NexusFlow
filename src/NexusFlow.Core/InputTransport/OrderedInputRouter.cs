using System.Collections.Concurrent;
using NexusFlow.Protocol.Input;

namespace NexusFlow.Core.InputTransport;

public sealed class OrderedInputRouter
{
	private readonly ConcurrentDictionary<string, OrderedInputInbox> _inboxes = new();
	private readonly long _maxBuffer;

	public OrderedInputRouter(long maxBuffer = 2048)
	{
		_maxBuffer = maxBuffer;
	}

	public OrderedInputInbox GetInbox(string fromPeerId) =>
		_inboxes.GetOrAdd(fromPeerId, _ => new OrderedInputInbox(startExpectedSeq: 1, maxBuffer: _maxBuffer));

	public void Push(InputEventV1 ev, Action<InputEventV1> apply)
	{
		var inbox = GetInbox(ev.FromPeerId);
		inbox.Push(ev, apply);
	}
}
