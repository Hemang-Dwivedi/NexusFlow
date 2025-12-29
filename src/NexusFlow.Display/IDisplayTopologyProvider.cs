using NexusFlow.Display.Models;

namespace NexusFlow.Display;

public interface IDisplayTopologyProvider
{
	PeerDisplayCluster GetLocalCluster();
	event EventHandler? TopologyChanged; // fire on hotplug/resolution change
}
