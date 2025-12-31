namespace NexusFlow.Core.Routing;

public interface IRoutingEngine
{
	string ActiveTargetPeerId { get; }
	string ActiveSourcePeerId { get; }

	event EventHandler<string>? ActiveTargetChanged;
	event EventHandler<string>? ActiveSourceChanged;

	Task RequestSetActiveTargetAsync(string targetPeerId, CancellationToken ct = default);
	Task RequestSetActiveSourceAsync(string sourcePeerId, CancellationToken ct = default);

	// V2 helpers
	(string ActiveTargetPeerId, NexusFlow.Protocol.Control.LamportStamp TargetStamp,
	 string ActiveSourcePeerId, NexusFlow.Protocol.Control.LamportStamp SourceStamp) GetSnapshotV2();

	void ApplyRemoteV2(object msg);
	Task HandlePeerDisconnectedAsync(string peerId, CancellationToken ct = default);
}
