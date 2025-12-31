namespace NexusFlow.Protocol.Control;

public sealed record ControlHello(string PeerId, string DeviceName, Guid SessionId, byte[] Nonce, int ProtocolVersion);
public sealed record ControlAuth(Guid SessionId, string PeerId, byte[] Mac);
public sealed record ControlResult(Guid SessionId, bool Accepted, string? Reason);
public sealed record Ping(long TicksUtc);
public sealed record Pong(long TicksUtc);
public sealed record SetActiveTarget(string TargetPeerId);
public sealed record SetActiveSource(string SourcePeerId);
public sealed record RoutingStateSync(string ActiveTargetPeerId, string ActiveSourcePeerId);