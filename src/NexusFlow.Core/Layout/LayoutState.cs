namespace NexusFlow.Core.Layout;

public sealed class LayoutState : ILayoutState
{
	private LayoutSnapshot? _current;
	public LayoutSnapshot? Current => _current;

	public event Action<LayoutSnapshot?>? Changed;

	public void Set(LayoutSnapshot? snapshot)
	{
		_current = snapshot;
		Changed?.Invoke(_current);
	}
}
