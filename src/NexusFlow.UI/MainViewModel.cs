using CommunityToolkit.Mvvm.ComponentModel;

namespace NexusFlow.UI.ViewModels;

public sealed class MainViewModel
{
	public LayoutEditorViewModel Layout { get; }
	public PeersListViewModel Peers { get; }
	public DiagnosticsViewModel Diagnostics { get; }
	public string Title => "NexusFlow";

	public MainViewModel(
		LayoutEditorViewModel layout,
		PeersListViewModel peers,
		DiagnosticsViewModel diagnostics)
	{
		Layout = layout;
		Peers = peers;
		Diagnostics = diagnostics;
	}
}

