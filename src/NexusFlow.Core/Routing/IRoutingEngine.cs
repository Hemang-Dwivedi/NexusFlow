using NexusFlow.Protocol.Control;

namespace NexusFlow.Core.Routing;

public interface IRoutingEngine
{
	string ActiveTargetPeerId { get; }
	string ActiveSourcePeerId { get; }

	event EventHandler<string>? ActiveTargetChanged;
	event EventHandler<string>? ActiveSourceChanged;

	Task RequestSetActiveTargetAsync(string targetPeerId, CancellationToken ct = default);
	Task RequestSetActiveSourceAsync(string sourcePeerId, CancellationToken ct = default);

	(string ActiveTargetPeerId, LamportStamp TargetStamp,
	 string ActiveSourcePeerId, LamportStamp SourceStamp) GetSnapshotV2();

	RoutingApplyResult TryApplyRemoteV2(object msg);
	Task HandlePeerDisconnectedAsync(string peerId, CancellationToken ct = default);
}
