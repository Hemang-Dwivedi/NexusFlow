namespace NexusFlow.Protocol.Routing;

public interface IControlMessage;

public sealed record SetActiveTarget(string TargetPeerId);
public sealed record SetActiveSource(string SourcePeerId);

