using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NexusFlow.Core.Services;
using NexusFlow.Display.Layout;
using NexusFlow.Display.Models;
using NexusFlow.Settings.Layout;

namespace NexusFlow.UI.ViewModels;

public partial class LayoutEditorViewModel : ObservableObject
{
	private readonly DisplayService _displayService;

	

	public PeerDisplayCluster LocalCluster { get; }
	public NormalizedCluster Normalized { get; }

	public double CanvasWidth { get; }
	public double CanvasHeight { get; }

	// ---- Applied (committed) ----
	[ObservableProperty] private double appliedOffsetX;
	[ObservableProperty] private double appliedOffsetY;

	// ---- Draft (editable) ----
	[ObservableProperty] private double draftOffsetX;
	[ObservableProperty] private double draftOffsetY;

	// ---- UI state ----
	[ObservableProperty] private bool isDirty;
	// Drag state
	public bool IsDragging { get; private set; }
	private double _dragStartMouseX, _dragStartMouseY;
	private double _dragStartDraftX, _dragStartDraftY;
	// Cluster bounds in normalized space (no draft offset applied)
	private double _clusterMinX, _clusterMinY, _clusterMaxX, _clusterMaxY;
	private const double SnapThreshold = 12;
	private const double SnapMargin = 8;
	partial void OnDraftOffsetXChanged(double value) => RefreshDirtyState();
	partial void OnDraftOffsetYChanged(double value) => RefreshDirtyState();
	partial void OnAppliedOffsetXChanged(double value) => RefreshDirtyState();
	partial void OnAppliedOffsetYChanged(double value) => RefreshDirtyState();
	private readonly ILayoutStore _layoutStore;
	private readonly LayoutState _layoutState;
	private readonly string _peerId;
	#region Constructor

	public LayoutEditorViewModel(DisplayService displayService, ILayoutStore layoutStore)
	{
		_displayService = displayService;
		_layoutStore = layoutStore;

		LocalCluster = _displayService.GetLocalCluster();
		_peerId = LocalCluster.PeerId;

		CanvasWidth = 900;
		CanvasHeight = 400;
		Normalized = DisplayLayoutNormalizer.Normalize(LocalCluster, CanvasWidth, CanvasHeight);

		UpdateClusterBounds();

		// Load persisted state
		_layoutState = _layoutStore.Load();

		if (_layoutState.Peers.TryGetValue(_peerId, out var peer))
		{
			AppliedOffsetX = peer.AppliedOffsetX;
			AppliedOffsetY = peer.AppliedOffsetY;
			DraftOffsetX = peer.AppliedOffsetX;
			DraftOffsetY = peer.AppliedOffsetY;
		}
		else
		{
			AppliedOffsetX = 0;
			AppliedOffsetY = 0;
			DraftOffsetX = 0;
			DraftOffsetY = 0;
		}

		RefreshDirtyState();
	}
	#endregion

	private void RefreshDirtyState()
	{
		IsDirty = Math.Abs(DraftOffsetX - AppliedOffsetX) > 0.01
			   || Math.Abs(DraftOffsetY - AppliedOffsetY) > 0.01;

		ApplyCommand.NotifyCanExecuteChanged();
		RevertCommand.NotifyCanExecuteChanged();
	}

	private void UpdateClusterBounds()
	{
		if (Normalized.Displays.Count == 0)
		{
			_clusterMinX = _clusterMinY = _clusterMaxX = _clusterMaxY = 0;
			return;
		}

		_clusterMinX = Normalized.Displays.Min(d => d.Nx);
		_clusterMinY = Normalized.Displays.Min(d => d.Ny);
		_clusterMaxX = Normalized.Displays.Max(d => d.Nx + d.Nw);
		_clusterMaxY = Normalized.Displays.Max(d => d.Ny + d.Nh);
	}

	// ---- Dragging edits DRAFT only ----
	public void BeginDrag(double mouseX, double mouseY)
	{
		IsDragging = true;
		_dragStartMouseX = mouseX;
		_dragStartMouseY = mouseY;
		_dragStartDraftX = DraftOffsetX;
		_dragStartDraftY = DraftOffsetY;
	}

	public void DragTo(double mouseX, double mouseY)
	{
		if (!IsDragging) return;

		var proposedX = _dragStartDraftX + (mouseX - _dragStartMouseX);
		var proposedY = _dragStartDraftY + (mouseY - _dragStartMouseY);

		// Clamp (with margin)
		var minOffsetX = -_clusterMinX + SnapMargin;
		var maxOffsetX = CanvasWidth - _clusterMaxX - SnapMargin;

		var minOffsetY = -_clusterMinY + SnapMargin;
		var maxOffsetY = CanvasHeight - _clusterMaxY - SnapMargin;

		if (maxOffsetX < minOffsetX) { minOffsetX = maxOffsetX = 0; }
		if (maxOffsetY < minOffsetY) { minOffsetY = maxOffsetY = 0; }

		var clampedX = Clamp(proposedX, minOffsetX, maxOffsetX);
		var clampedY = Clamp(proposedY, minOffsetY, maxOffsetY);

		// Snap-to-edge
		clampedX = ApplySnapX(clampedX, minOffsetX, maxOffsetX);
		clampedY = ApplySnapY(clampedY, minOffsetY, maxOffsetY);

		DraftOffsetX = clampedX;
		DraftOffsetY = clampedY;
	}

	public void EndDrag() => IsDragging = false;

	private static double Clamp(double v, double min, double max)
		=> v < min ? min : (v > max ? max : v);

	private double ApplySnapX(double offsetX, double minOffsetX, double maxOffsetX)
	{
		var left = _clusterMinX + offsetX;
		var right = _clusterMaxX + offsetX;

		var targetLeft = SnapMargin;
		if (Math.Abs(left - targetLeft) <= SnapThreshold)
			offsetX += (targetLeft - left);

		var targetRight = CanvasWidth - SnapMargin;
		if (Math.Abs(right - targetRight) <= SnapThreshold)
			offsetX += (targetRight - right);

		return Clamp(offsetX, minOffsetX, maxOffsetX);
	}

	private double ApplySnapY(double offsetY, double minOffsetY, double maxOffsetY)
	{
		var top = _clusterMinY + offsetY;
		var bottom = _clusterMaxY + offsetY;

		var targetTop = SnapMargin;
		if (Math.Abs(top - targetTop) <= SnapThreshold)
			offsetY += (targetTop - top);

		var targetBottom = CanvasHeight - SnapMargin;
		if (Math.Abs(bottom - targetBottom) <= SnapThreshold)
			offsetY += (targetBottom - bottom);

		return Clamp(offsetY, minOffsetY, maxOffsetY);
	}

	// ---- Commands ----
	[RelayCommand(CanExecute = nameof(CanApply))]
	private void Apply()
	{
		AppliedOffsetX = DraftOffsetX;
		AppliedOffsetY = DraftOffsetY;

		// Persist
		if (!_layoutState.Peers.TryGetValue(_peerId, out var peer))
		{
			peer = new PeerLayoutState();
			_layoutState.Peers[_peerId] = peer;
		}

		peer.AppliedOffsetX = AppliedOffsetX;
		peer.AppliedOffsetY = AppliedOffsetY;

		peer.DisplayStableIds = LocalCluster.Displays
			.Select(d => d.StableId)
			.ToList();

		_layoutStore.Save(_layoutState);
	}


	private bool CanApply() => IsDirty;

	[RelayCommand(CanExecute = nameof(CanRevert))]
	private void Revert()
	{
		DraftOffsetX = AppliedOffsetX;
		DraftOffsetY = AppliedOffsetY;
	}

	private bool CanRevert() => IsDirty;
}
