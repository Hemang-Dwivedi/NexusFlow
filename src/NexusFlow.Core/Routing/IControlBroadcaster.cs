namespace NexusFlow.Core.Routing;

public interface IControlBroadcaster
{
	Task BroadcastAsync(object controlMessage, CancellationToken ct = default);
	Task SendToPeerAsync(string peerId, object controlMessage, CancellationToken ct = default);
}
