using System;

namespace NexusFlow.Identity;

public sealed class LocalIdentity : ILocalIdentity
{
	public string PeerId { get; } = Guid.NewGuid().ToString("D"); // TODO: persist
	public string DeviceName { get; } = Environment.MachineName;
}
