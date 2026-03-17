using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NexusFlow.Core.Layout;
using NexusFlow.Core.Routing;
using NexusFlow.Core.Services;
using NexusFlow.Identity;
using NexusFlow.Settings.Layout;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace NexusFlow.UI.ViewModels;

/// <summary>
/// Windows-Display-Settings-style layout editor.
///
/// Local peer: each physical display as its own tile, laid out in a horizontal
/// row sorted left-to-right by virtual X.  This is robust to clone/mirror mode
/// because tile X positions come from cx (sequential), not raw virtual coords.
///
/// Remote peers: one draggable tile per peer, sized to fit within the right-hand
/// area of the canvas.  On Apply the canvas position is translated back to virtual
/// desktop coordinates so the routing engine stays consistent.
/// </summary>
public partial class LayoutEditorViewModel : ObservableObject, IDisposable
{
	private readonly ILayoutState _layout;
	private readonly ILayoutStore _layoutStore;
	private readonly IRoutingEngine _routing;
	private readonly DisplayService _displayService;
	private readonly string _localPeerId;
	private NexusFlow.Settings.Layout.LayoutState _persisted;

	public ObservableCollection<DisplayTileVm> DisplayTiles { get; } = new();
	public ObservableCollection<PeerGroupVm> PeerGroups { get; } = new();

	public double CanvasWidth  { get; } = 900;
	public double CanvasHeight { get; } = 400;
	public double WorkspaceWidth  => CanvasWidth;
	public double WorkspaceHeight => CanvasHeight;

	// Canvas-coordinate positions per remote peer: (appliedX, appliedY, draftX, draftY)
	private readonly Dictionary<string, (double ax, double ay, double dx, double dy)> _positions = new();

	// Virtual positions from store (lazy conversion happens inside RefreshLayout)
	private readonly Dictionary<string, (double vx, double vy)> _storedVirtualPositions = new();

	// Raw PeerRects from ILayoutState (remote peers only)
	private readonly Dictionary<string, PeerRect> _rawRects = new();

	// Local cluster canvas parameters (set every RefreshLayout)
	private double _localOriginX;        // canvas X of the row's left tile
	private double _localOriginY;        // canvas Y of the row's top edge
	private double _localScale = 1.0;    // virtual px -> canvas px
	private double _localMinX, _localMinY; // Windows virtual-desktop origin of local cluster
	private double _localCanvasRight;    // right edge of the rightmost local tile
	private double _rowH;                // canvas height of the tallest local display

	[ObservableProperty] private bool isDirty;
	[ObservableProperty] private string activeTargetName = "Routing to: Local";

	// Drag state
	public bool IsDragging { get; private set; }
	private string? _dragPeerId;
	private double _dragStartMouseX, _dragStartMouseY;
	private double _dragStartDraftX, _dragStartDraftY;

	public LayoutEditorViewModel(
		ILayoutState layout,
		ILayoutStore layoutStore,
		IRoutingEngine routing,
		ILocalIdentity me,
		DisplayService displayService)
	{
		_layout = layout;
		_layoutStore = layoutStore;
		_routing = routing;
		_localPeerId = me.PeerId;
		_displayService = displayService;

		_persisted = _layoutStore.Load();
		LoadStoredPositions();

		_layout.Changed += OnLayoutChanged;
		_routing.ActiveTargetChanged += OnActiveTargetChanged;

		RefreshLayout(_layout.Current);
	}

	public void Dispose()
	{
		_layout.Changed -= OnLayoutChanged;
		_routing.ActiveTargetChanged -= OnActiveTargetChanged;
	}

	private void OnLayoutChanged(LayoutSnapshot? snap)
		=> Dispatcher.UIThread.Post(() => RefreshLayout(snap));

	private void OnActiveTargetChanged(object? sender, string peerId)
	{
		Dispatcher.UIThread.Post(() =>
		{
			foreach (var t in DisplayTiles)  t.IsActiveTarget = t.PeerId == peerId;
			foreach (var g in PeerGroups)    g.IsActiveTarget = g.PeerId == peerId;
			var name = PeerGroups.FirstOrDefault(g => g.IsActiveTarget)?.PeerName
				?? (peerId == _localPeerId ? "Local" : peerId[..Math.Min(8, peerId.Length)]);
			ActiveTargetName = $"Routing to: {name}";
		});
	}

	private void LoadStoredPositions()
	{
		_storedVirtualPositions.Clear();
		foreach (var kv in _persisted.Peers)
		{
			if (!kv.Value.HasSavedPosition) continue;
			_storedVirtualPositions[kv.Key] = (kv.Value.AppliedOffsetX, kv.Value.AppliedOffsetY);
		}
	}

	private void RefreshLayout(LayoutSnapshot? snap)
	{
		// 1. Collect remote peer rects
		_rawRects.Clear();
		if (snap != null)
		{
			foreach (var r in snap.Peers)
				if (r.PeerId != _localPeerId)
					_rawRects[r.PeerId] = r;
		}

		// 2. Get local displays
		var localCluster = _displayService.GetLocalCluster();
		if (localCluster.Displays.Count == 0)
		{
			DisplayTiles.Clear();
			PeerGroups.Clear();
			RefreshDirtyState();
			return;
		}

		// Virtual-desktop origin of local cluster (needed for Apply canvas -> virtual math)
		_localMinX = localCluster.Displays.Min(d => (double)d.X);
		_localMinY = localCluster.Displays.Min(d => (double)d.Y);

		// 3. Compute horizontal-row scale for local displays
		//    Sort by virtual X so monitors appear left-to-right regardless of Windows numbering.
		var sorted = localCluster.Displays.OrderBy(d => d.X).ThenBy(d => d.Y).ToList();

		const double maxRowH  = 240.0;   // max height of local monitor row on canvas
		const double maxRowW  = 440.0;   // max total width of local monitor row on canvas
		const double tileGap  = 10.0;    // gap between adjacent tiles
		const double minTileW = 80.0;    // minimum tile width  (always visible)
		const double minTileH = 52.0;    // minimum tile height (always visible)

		double totalVW = sorted.Sum(d => (double)d.Width);
		double maxVH   = sorted.Max(d => (double)d.Height);

		double scaleH = maxRowH / maxVH;
		double gapTotal = tileGap * Math.Max(0, sorted.Count - 1);
		double scaleW = (maxRowW - gapTotal) / totalVW;
		_localScale = Math.Max(0.01, Math.Min(scaleH, scaleW));

		_rowH = maxVH * _localScale;
		_localOriginX = 24;
		_localOriginY = (CanvasHeight - _rowH) / 2.0;

		// 4. Build local display tiles (side-by-side, never overlap)
		var newTiles = new List<DisplayTileVm>();
		double cx = _localOriginX;
		foreach (var d in sorted)
		{
			double nw = Math.Max(minTileW, d.Width  * _localScale);
			double nh = Math.Max(minTileH, d.Height * _localScale);
			double ny = _localOriginY + (_rowH - nh) / 2.0;  // centre shorter monitors vertically

			newTiles.Add(new DisplayTileVm(
				_localPeerId, localCluster.PeerName,
				d.DisplayNumber, isLocal: true, d.IsPrimary, d.StableId,
				d.X, d.Y, d.Width, d.Height)
			{
				Nx = cx,
				Ny = ny,
				Nw = nw,
				Nh = nh,
				IsActiveTarget = _localPeerId == _routing.ActiveTargetPeerId
			});
			cx += nw + tileGap;
		}
		_localCanvasRight = cx - tileGap;   // right edge of the last local tile

		// 5. Build remote peer tiles
		//    Scale each remote tile so it fits in the space right of the local cluster.
		double remoteAreaW = CanvasWidth  - _localCanvasRight - 30;  // available width
		double remoteAreaH = CanvasHeight - 20;                       // available height

		foreach (var kv in _rawRects)
		{
			var rect = kv.Value;

			// Scale remote tile to fit in the right-hand area (also never exceed local scale)
			double rScaleW = remoteAreaW / Math.Max(1, rect.Width);
			double rScaleH = remoteAreaH / Math.Max(1, rect.Height);
			double rScale  = Math.Min(_localScale, Math.Min(rScaleW, rScaleH));
			rScale = Math.Max(rScale, 0.01);

			double rNw = Math.Max(minTileW, rect.Width  * rScale);
			double rNh = Math.Max(minTileH, rect.Height * rScale);

			// Initialise canvas position (from persisted store, or default: right of local row)
			if (!_positions.ContainsKey(rect.PeerId))
			{
				double canvasX, canvasY;
				if (_storedVirtualPositions.TryGetValue(rect.PeerId, out var sv))
				{
					// Re-derive canvas coords from stored virtual position
					canvasX = _localOriginX + (sv.vx - _localMinX) * _localScale;
					canvasY = _localOriginY + (sv.vy - _localMinY) * _localScale;
				}
				else
				{
					// Default: just to the right of local cluster, vertically centred
					canvasX = _localCanvasRight + 20;
					canvasY = _localOriginY + (_rowH - rNh) / 2.0;
				}
				// Clamp to keep tile fully visible
				canvasX = Math.Max(0, Math.Min(CanvasWidth  - rNw, canvasX));
				canvasY = Math.Max(0, Math.Min(CanvasHeight - rNh, canvasY));
				_positions[rect.PeerId] = (canvasX, canvasY, canvasX, canvasY);
			}

			var pos  = _positions[rect.PeerId];
			var name = string.IsNullOrEmpty(rect.DeviceName)
				? rect.PeerId[..Math.Min(8, rect.PeerId.Length)]
				: rect.DeviceName;

			newTiles.Add(new DisplayTileVm(
				rect.PeerId, name, 1, isLocal: false, isPrimary: true, "remote",
				rect.X, rect.Y, rect.Width, rect.Height)
			{
				Nx = pos.dx,
				Ny = pos.dy,
				Nw = rNw,
				Nh = rNh,
				IsActiveTarget = rect.PeerId == _routing.ActiveTargetPeerId
			});
		}

		// 6. Build peer group labels
		var newGroups = new List<PeerGroupVm>();
		double localRowWidth = _localCanvasRight - _localOriginX;

		// Local label — below the row
		newGroups.Add(new PeerGroupVm(_localPeerId, localCluster.PeerName, isLocal: true)
		{
			LabelX     = _localOriginX,
			LabelY     = _localOriginY + _rowH + 6,
			LabelWidth = Math.Max(60, localRowWidth),
			IsActiveTarget = _localPeerId == _routing.ActiveTargetPeerId
		});

		// Remote labels — below each remote tile
		foreach (var kv in _rawRects)
		{
			var rect = kv.Value;
			if (!_positions.TryGetValue(rect.PeerId, out var pos)) continue;

			double rScaleW = remoteAreaW / Math.Max(1, rect.Width);
			double rScaleH = remoteAreaH / Math.Max(1, rect.Height);
			double rScale  = Math.Min(_localScale, Math.Min(rScaleW, rScaleH));
			rScale = Math.Max(rScale, 0.01);
			double rNw = Math.Max(minTileW, rect.Width  * rScale);
			double rNh = Math.Max(minTileH, rect.Height * rScale);

			var name = string.IsNullOrEmpty(rect.DeviceName)
				? rect.PeerId[..Math.Min(8, rect.PeerId.Length)]
				: rect.DeviceName;

			newGroups.Add(new PeerGroupVm(rect.PeerId, name, isLocal: false)
			{
				LabelX     = pos.dx,
				LabelY     = pos.dy + rNh + 6,
				LabelWidth = Math.Max(60, rNw),
				IsActiveTarget = rect.PeerId == _routing.ActiveTargetPeerId
			});
		}

		// 7. Commit to UI
		DisplayTiles.Clear();
		foreach (var t in newTiles) DisplayTiles.Add(t);
		PeerGroups.Clear();
		foreach (var g in newGroups) PeerGroups.Add(g);

		var activeName = PeerGroups.FirstOrDefault(g => g.IsActiveTarget)?.PeerName ?? "Local";
		ActiveTargetName = $"Routing to: {activeName}";

		RefreshDirtyState();
	}

	// ── Drag ─────────────────────────────────────────────────────────────────

	public void BeginDrag(string peerId, double mouseX, double mouseY)
	{
		if (peerId == _localPeerId) return;
		IsDragging = true;
		_dragPeerId = peerId;
		_dragStartMouseX = mouseX;
		_dragStartMouseY = mouseY;

		var pos = _positions.TryGetValue(peerId, out var v) ? v : default;
		_dragStartDraftX = pos.dx;
		_dragStartDraftY = pos.dy;
	}

	public void DragTo(double mouseX, double mouseY)
	{
		if (!IsDragging || string.IsNullOrWhiteSpace(_dragPeerId)) return;

		var newDx = _dragStartDraftX + (mouseX - _dragStartMouseX);
		var newDy = _dragStartDraftY + (mouseY - _dragStartMouseY);

		if (_positions.TryGetValue(_dragPeerId, out var cur))
			_positions[_dragPeerId] = (cur.ax, cur.ay, newDx, newDy);
		else
			_positions[_dragPeerId] = (newDx, newDy, newDx, newDy);

		RefreshLayout(_layout.Current);
	}

	public void EndDrag()
	{
		IsDragging = false;
		_dragPeerId = null;
	}

	// ── Commands ──────────────────────────────────────────────────────────────

	private void RefreshDirtyState()
	{
		IsDirty = _positions.Values.Any(v =>
			Math.Abs(v.dx - v.ax) > 0.5 ||
			Math.Abs(v.dy - v.ay) > 0.5);
		ApplyCommand.NotifyCanExecuteChanged();
		RevertCommand.NotifyCanExecuteChanged();
	}

	[RelayCommand(CanExecute = nameof(CanApply))]
	private void Apply()
	{
		foreach (var (peerId, v) in _positions.ToList())
		{
			_positions[peerId] = (v.dx, v.dy, v.dx, v.dy);

			if (!_persisted.Peers.TryGetValue(peerId, out var peerState))
			{
				peerState = new PeerLayoutState();
				_persisted.Peers[peerId] = peerState;
			}

			// Canvas -> virtual: inverse of canvas = localOrigin + (virtual - localMin) * scale
			double virtualX = _localMinX + (v.dx - _localOriginX) / Math.Max(0.0001, _localScale);
			double virtualY = _localMinY + (v.dy - _localOriginY) / Math.Max(0.0001, _localScale);

			peerState.AppliedOffsetX  = virtualX;
			peerState.AppliedOffsetY  = virtualY;
			peerState.HasSavedPosition = true;

			// Push the new position into the routing engine immediately
			if (peerId != _localPeerId && _rawRects.TryGetValue(peerId, out var rawRect))
			{
				_layout.UpsertPeerRect(new PeerRect(
					PeerId:     peerId,
					DeviceName: rawRect.DeviceName,
					X:          virtualX,
					Y:          virtualY,
					Width:      rawRect.Width,
					Height:     rawRect.Height));
			}
		}

		_layoutStore.Save(_persisted);
		RefreshDirtyState();
	}

	private bool CanApply() => IsDirty;

	[RelayCommand(CanExecute = nameof(CanRevert))]
	private void Revert()
	{
		foreach (var peerId in _positions.Keys.ToList())
		{
			var v = _positions[peerId];
			_positions[peerId] = (v.ax, v.ay, v.ax, v.ay);
		}
		RefreshLayout(_layout.Current);
		RefreshDirtyState();
	}

	private bool CanRevert() => IsDirty;
}

// ── Tile VM ──────────────────────────────────────────────────────────────────

public sealed partial class DisplayTileVm : ObservableObject
{
	public string PeerId        { get; }
	public string PeerName      { get; }
	public int    DisplayNumber { get; }
	public bool   IsLocal       { get; }
	public bool   IsPrimary     { get; }
	public string StableId      { get; }
	public double VirtualX      { get; }
	public double VirtualY      { get; }
	public double VirtualW      { get; }
	public double VirtualH      { get; }

	[ObservableProperty] private double nx;
	[ObservableProperty] private double ny;
	[ObservableProperty] private double nw;
	[ObservableProperty] private double nh;
	[ObservableProperty] private bool   isActiveTarget;

	public DisplayTileVm(string peerId, string peerName, int displayNumber,
		bool isLocal, bool isPrimary, string stableId,
		double virtualX, double virtualY, double virtualW, double virtualH)
	{
		PeerId = peerId; PeerName = peerName; DisplayNumber = displayNumber;
		IsLocal = isLocal; IsPrimary = isPrimary; StableId = stableId;
		VirtualX = virtualX; VirtualY = virtualY; VirtualW = virtualW; VirtualH = virtualH;
	}
}

// ── Group label VM ────────────────────────────────────────────────────────────

public sealed partial class PeerGroupVm : ObservableObject
{
	public string PeerId   { get; }
	public string PeerName { get; }
	public bool   IsLocal  { get; }

	[ObservableProperty] private double labelX;
	[ObservableProperty] private double labelY;
	[ObservableProperty] private double labelWidth;
	[ObservableProperty] private bool   isActiveTarget;

	public PeerGroupVm(string peerId, string peerName, bool isLocal)
	{
		PeerId = peerId; PeerName = peerName; IsLocal = isLocal;
	}
}
