namespace NexusFlow.Protocol.Control;

/// <summary>
/// Minimal layout/topology sync message.
/// Sent on the authenticated Control channel.
/// All values are in real pixels (virtual-desktop coordinate space).
/// </summary>
public sealed record PeerRectSyncV1(
	string PeerId,
	int MinX,
	int MinY,
	int Width,
	int Height
);
