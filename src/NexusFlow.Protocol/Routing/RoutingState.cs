namespace NexusFlow.Protocol.Routing;

public readonly record struct RoutingFieldState(string PeerId, LamportStamp Stamp);

public sealed record RoutingState(
	RoutingFieldState ActiveTarget,
	RoutingFieldState ActiveSource
);
