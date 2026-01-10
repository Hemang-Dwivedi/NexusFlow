namespace NexusFlow.Core.Discovery;

public interface IPeerEndpointResolver
{
	bool TryGetEndpoint(string peerId, out string host, out int port);
}
