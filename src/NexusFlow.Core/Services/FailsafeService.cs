using System.Threading;

namespace NexusFlow.Core.Services;

public sealed class FailsafeService : IFailsafeService
{
	private int _blocked; // 0/1

	public bool IsBlocked => Volatile.Read(ref _blocked) == 1;

	public event Action<bool>? Changed;

	public void Block()
	{
		if (Interlocked.Exchange(ref _blocked, 1) == 1) return;
		Changed?.Invoke(true);
	}

	public void Unblock()
	{
		if (Interlocked.Exchange(ref _blocked, 0) == 0) return;
		Changed?.Invoke(false);
	}

	public void Toggle()
	{
		if (IsBlocked) Unblock();
		else Block();
	}
}
