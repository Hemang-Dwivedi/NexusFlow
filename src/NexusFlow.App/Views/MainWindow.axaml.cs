using Avalonia.Controls;
using NexusFlow.Core.Services;
using NexusFlow.Display.Windows;
using NexusFlow.UI.ViewModels;
using NexusFlow.Settings.Layout;

namespace NexusFlow.App.Views;

public partial class MainWindow : Window
{
	public MainWindow()
	{
		InitializeComponent();

		var provider = new WindowsDisplayTopologyProvider();
		var displayService = new DisplayService(provider);
		var store = new JsonLayoutStore("NexusFlow");
		DataContext = new LayoutEditorViewModel(displayService, store);
	}
}
