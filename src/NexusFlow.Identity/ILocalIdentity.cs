namespace NexusFlow.Identity;

public interface ILocalIdentity
{
	string PeerId { get; }      // stable GUID string
	string DeviceName { get; }  // user-friendly
}
