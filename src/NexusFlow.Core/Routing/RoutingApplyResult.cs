namespace NexusFlow.Core.Routing;

public enum RoutingApplyDecision
{
	Applied,
	Ignored_FailsafeBlocked,
	Ignored_OlderStamp,
	Ignored_UnknownMessage
}

public readonly record struct RoutingApplyResult(RoutingApplyDecision Decision, string? Detail = null);
