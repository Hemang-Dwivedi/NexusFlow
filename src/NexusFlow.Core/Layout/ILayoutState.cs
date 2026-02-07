namespace NexusFlow.Core.Layout;

public interface ILayoutState
{
	LayoutSnapshot? Current { get; }
	event Action<LayoutSnapshot?> Changed;
	void Set(LayoutSnapshot? snapshot);
}
