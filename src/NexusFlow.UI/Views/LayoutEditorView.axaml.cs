using Avalonia.Controls;
using Avalonia.Input;
using NexusFlow.UI.ViewModels;

namespace NexusFlow.UI.Views;

public partial class LayoutEditorView : UserControl
{
	public LayoutEditorView()
	{
		InitializeComponent();
	}

	private LayoutEditorViewModel? Vm => DataContext as LayoutEditorViewModel;

	public void TilePointerPressed(object? sender, PointerPressedEventArgs e)
	{
		if (Vm is null) return;
		if (sender is not Control c) return;
		if (c.DataContext is not DisplayTileVm tile) return;

		var p = e.GetPosition(this);
		Vm.BeginDrag(tile, p.X, p.Y);
		e.Pointer.Capture(c);
		e.Handled = true;
	}

	public void TilePointerMoved(object? sender, PointerEventArgs e)
	{
		if (Vm is null || !Vm.IsDragging) return;
		var p = e.GetPosition(this);
		Vm.DragTo(p.X, p.Y);
		e.Handled = true;
	}

	public void TilePointerReleased(object? sender, PointerReleasedEventArgs e)
	{
		if (Vm is null) return;
		Vm.EndDrag();
		e.Pointer.Capture(null);
		e.Handled = true;
	}
}
