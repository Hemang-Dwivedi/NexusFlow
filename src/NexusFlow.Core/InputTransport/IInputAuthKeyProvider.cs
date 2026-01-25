namespace NexusFlow.Core.InputTransport;

/// <summary>
/// Provides a per-peer InputAuthKey derived from the authenticated Control session.
/// If this returns false, there is no active authenticated Control session.
/// </summary>
public interface IInputAuthKeyProvider
{
	bool TryGetInputAuthKey(string peerId, out byte[] key);
}
