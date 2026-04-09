using NexusFlow.Protocol.Control;

namespace NexusFlow.Core.Routing;

public interface IRoutingEngine
{
	string ActiveTargetPeerId { get; }
	string ActiveSourcePeerId { get; }

	event EventHandler<string>? ActiveTargetChanged;
	event EventHandler<string>? ActiveSourceChanged;

	/// <summary>
	/// Fires on the receiving peer when a remote switch makes it the active target,
	/// carrying the entry edge and normalized position (0–1) along that edge.
	/// The subscriber should warp the local cursor to that entry point.
	/// </summary>
	event Action<EntryEdge, double>? CursorWarpRequested;

	Task RequestSetActiveTargetAsync(string targetPeerId, CancellationToken ct = default);
	Task RequestSetActiveTargetAsync(string targetPeerId, EntryEdge entryEdge, double entryNormalized, CancellationToken ct = default);
	Task RequestSetActiveSourceAsync(string sourcePeerId, CancellationToken ct = default);

	(string ActiveTargetPeerId, LamportStamp TargetStamp,
	 string ActiveSourcePeerId, LamportStamp SourceStamp) GetSnapshotV2();

	RoutingApplyResult TryApplyRemoteV2(object msg);
	Task HandlePeerDisconnectedAsync(string peerId, CancellationToken ct = default);
}
