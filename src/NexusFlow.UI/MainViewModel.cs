using CommunityToolkit.Mvvm.ComponentModel;

namespace NexusFlow.UI.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
	public LayoutEditorViewModel Layout { get; }
	public PeersListViewModel Peers { get; }
	public string Title => "NexusFlow";
	public MainViewModel(LayoutEditorViewModel layout, PeersListViewModel peers)
	{
		Layout = layout;
		Peers = peers;
	}
}
