using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NexusFlow.Core.Services;
using NexusFlow.Display.Layout;
using NexusFlow.Display.Models;
using NexusFlow.Settings.Layout;
using NexusFlow.UI.Services;
using System.Collections.ObjectModel;

namespace NexusFlow.UI.ViewModels;

public partial class LayoutEditorViewModel : ObservableObject
{
	private readonly DisplayService _displayService;


	[ObservableProperty] private double scale;
	[ObservableProperty] private double panX;
	[ObservableProperty] private double panY;

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
	partial void OnDraftOffsetXChanged(double value) { RefreshDirtyState(); RecomputeTransform(); }
	partial void OnDraftOffsetYChanged(double value) { RefreshDirtyState(); RecomputeTransform(); }
	partial void OnAppliedOffsetXChanged(double value) => RefreshDirtyState();
	partial void OnAppliedOffsetYChanged(double value) => RefreshDirtyState();
	private readonly ILayoutStore _layoutStore;
	private readonly LayoutState _layoutState;
	private readonly string _peerId;
	private readonly NexusFlow.Core.Layout.IRuntimeLayoutState _runtimeLayout;
	private readonly IConnectedPeersSnapshot _connectedPeers;

	public ObservableCollection<PeerChoiceVm> PeerChoices { get; } = new();

	[ObservableProperty] private PeerChoiceVm? selectedPeer;


	#region Constructor

	    public LayoutEditorViewModel(DisplayService displayService, ILayoutStore layoutStore, IConnectedPeersSnapshot connectedPeers)
    {
        _displayService = displayService;
        _layoutStore = layoutStore;
        _connectedPeers = connectedPeers;

        LocalCluster = _displayService.GetLocalCluster();
        _peerId = LocalCluster.PeerId;

        CanvasWidth = 900;
        CanvasHeight = 400;
        Normalized = DisplayLayoutNormalizer.Normalize(LocalCluster, CanvasWidth, CanvasHeight);
		
		UpdateClusterBounds();

        _layoutState = _layoutStore.Load(); // your existing load logic

        // peer dropdown
        RefreshPeerChoices();
        _connectedPeers.Changed += OnConnectedPeersChanged;

        // default selection = local
        selectedPeer = PeerChoices.FirstOrDefault(p => p.PeerId == _peerId);
    }

    


	#endregion
	private void PublishRuntimeLayout()
	{
		// Local peer rect in CANVAS coordinates after applied offsets
		var x = _clusterMinX + AppliedOffsetX;
		var y = _clusterMinY + AppliedOffsetY;
		var w = _clusterMaxX - _clusterMinX;
		var h = _clusterMaxY - _clusterMinY;

		var snap = new NexusFlow.Core.Layout.LayoutSnapshot(new[]
		{
		new NexusFlow.Core.Layout.PeerRect(_peerId, x, y, w, h)
	});

		_runtimeLayout.Set(snap);
	}

	private void OnConnectedPeersChanged()
		=> Avalonia.Threading.Dispatcher.UIThread.Post(RefreshPeerChoices);

	private void RefreshPeerChoices()
	{
		var snapshot = _connectedPeers.Snapshot();

		PeerChoices.Clear();

		// Always include local first
		PeerChoices.Add(new PeerChoiceVm(_peerId, $"{LocalCluster.PeerName} (This PC)", isLocal: true));

		foreach (var p in snapshot.OrderBy(x => x.DeviceName))
		{
			// exclude local if it ever appears
			if (p.PeerId == _peerId) continue;
			PeerChoices.Add(new PeerChoiceVm(p.PeerId, p.DeviceName, isLocal: false));
		}
	}
	private void RefreshDirtyState()
	{
		IsDirty = Math.Abs(DraftOffsetX - AppliedOffsetX) > 0.01
			   || Math.Abs(DraftOffsetY - AppliedOffsetY) > 0.01;

		ApplyCommand.NotifyCanExecuteChanged();
		RevertCommand.NotifyCanExecuteChanged();
	}

	private void UpdateClusterBounds()
	{
		if (LocalCluster.Displays.Count == 0)
		{
			_clusterMinX = _clusterMinY = _clusterMaxX = _clusterMaxY = 0;
			return;
		}

		_clusterMinX = LocalCluster.Displays.Min(d => d.X);
		_clusterMinY = LocalCluster.Displays.Min(d => d.Y);
		_clusterMaxX = LocalCluster.Displays.Max(d => d.X + d.Width);
		_clusterMaxY = LocalCluster.Displays.Max(d => d.Y + d.Height);
	}

	private void RecomputeTransform()
	{
		// World bounds of LOCAL peer (for now)
		var worldMinX = _clusterMinX + DraftOffsetX;
		var worldMinY = _clusterMinY + DraftOffsetY;
		var worldMaxX = _clusterMaxX + DraftOffsetX;
		var worldMaxY = _clusterMaxY + DraftOffsetY;

		var worldW = Math.Max(1.0, worldMaxX - worldMinX);
		var worldH = Math.Max(1.0, worldMaxY - worldMinY);

		var sx = CanvasWidth / worldW;
		var sy = CanvasHeight / worldH;
		var s = Math.Min(sx, sy);

		Scale = s;

		// center it
		PanX = (CanvasWidth - worldW * s) / 2.0 - worldMinX * s;
		PanY = (CanvasHeight - worldH * s) / 2.0 - worldMinY * s;
	}

	private (double cx, double cy) WorldToCanvas(double wx, double wy)
		=> (wx * Scale + PanX, wy * Scale + PanY);

	private (double wx, double wy) CanvasToWorld(double cx, double cy)
		=> ((cx - PanX) / Scale, (cy - PanY) / Scale);



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

		// canvas delta -> world delta
		var dxCanvas = mouseX - _dragStartMouseX;
		var dyCanvas = mouseY - _dragStartMouseY;

		var dxWorld = dxCanvas / Math.Max(Scale, 0.00001);
		var dyWorld = dyCanvas / Math.Max(Scale, 0.00001);

		var proposedX = _dragStartDraftX + dxWorld;
		var proposedY = _dragStartDraftY + dyWorld;

		// Later, when multi-peer: clamp/snap in world. For now keep your logic simple:
		DraftOffsetX = proposedX;
		DraftOffsetY = proposedY;
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

		PublishRuntimeLayout();
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

public sealed record PeerChoiceVm(string PeerId, string DisplayName, bool isLocal);
public sealed class PeerBlockVm
{
	public string PeerId { get; }
	public string Name { get; }

	// World pixel offsets (persisted)
	public double WorldX { get; set; }
	public double WorldY { get; set; }

	// Rigid cluster bounds in LOCAL peer pixels (computed from displays)
	public double LocalMinX { get; init; }
	public double LocalMinY { get; init; }
	public double LocalMaxX { get; init; }
	public double LocalMaxY { get; init; }

	public PeerBlockVm(string peerId, string name) { PeerId = peerId; Name = name; }
}

