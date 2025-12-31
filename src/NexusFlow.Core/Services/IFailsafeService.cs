namespace NexusFlow.Core.Services;

public interface IFailsafeService
{
	bool IsBlocked { get; }
	event Action<bool /*isBlocked*/>? Changed;

	void Block();
	void Unblock();
	void Toggle();
}
