using Avalonia.Controls;
using Avalonia.Input;
using NexusFlow.UI.ViewModels;

namespace NexusFlow.UI.Views;

public partial class LayoutEditorView : UserControl
{
	public LayoutEditorView()
	{
		InitializeComponent();

		// Pointer handlers on the whole peer block container (named element in XAML)
		PeerDragSurface.PointerPressed += OnPointerPressed;
		PeerDragSurface.PointerMoved += OnPointerMoved;
		PeerDragSurface.PointerReleased += OnPointerReleased;
	}

	private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
	{
		if (DataContext is not LayoutEditorViewModel vm) return;
		var p = e.GetPosition(PeerDragSurface);
		vm.BeginDrag(p.X, p.Y);
		e.Pointer.Capture(PeerDragSurface);
		e.Handled = true;
	}

	private void OnPointerMoved(object? sender, PointerEventArgs e)
	{
		if (DataContext is not LayoutEditorViewModel vm || !vm.IsDragging) return;
		var p = e.GetPosition(PeerDragSurface);
		vm.DragTo(p.X, p.Y);
		e.Handled = true;
	}

	private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
	{
		if (DataContext is not LayoutEditorViewModel vm) return;
		vm.EndDrag();
		e.Pointer.Capture(null);
		e.Handled = true;
	}

}
