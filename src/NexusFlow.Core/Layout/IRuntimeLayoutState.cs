namespace NexusFlow.Core.Layout;

public interface IRuntimeLayoutState
{
	LayoutSnapshot? Current { get; }
	event Action<LayoutSnapshot?> Changed;
	void Set(LayoutSnapshot? snapshot);
}
