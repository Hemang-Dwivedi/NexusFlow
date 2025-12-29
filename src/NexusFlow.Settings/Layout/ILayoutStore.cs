namespace NexusFlow.Settings.Layout;

public interface ILayoutStore
{
	LayoutState Load();
	void Save(LayoutState state);
}
