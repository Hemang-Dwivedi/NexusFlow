using NexusFlow.Display;
using NexusFlow.Display.Models;

namespace NexusFlow.Core.Services;

public sealed class DisplayService
{
	private readonly IDisplayTopologyProvider _provider;

	public DisplayService(IDisplayTopologyProvider provider) => _provider = provider;

	public PeerDisplayCluster GetLocalCluster() => _provider.GetLocalCluster();
}
