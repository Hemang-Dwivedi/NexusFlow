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
/// Local peer: each physical display as an individual tile (from DisplayService).
/// Remote peers: one draggable tile per peer (bounding box from ILayoutState).
/// Positions stored as absolute virtual coords. Apply updates ILayoutState + ILayoutStore.
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

	public double CanvasWidth { get; } = 900;
	public double CanvasHeight { get; } = 400;
	public double WorkspaceWidth => CanvasWidth;
	public double WorkspaceHeight => CanvasHeight;

	// Absolute virtual position per remote peer: (appliedX, appliedY, draftX, draftY)
	private readonly Dictionary<string, (double ax, double ay, double dx, double dy)> _positions = new();

	// Raw PeerRects as reported by ILayoutState (before our position override)
	private readonly Dictionary<string, PeerRect> _rawRects = new();

	// Normalization state (canvas <-> virtual conversion)
	private double _lastMinX, _lastMinY;
	private double _lastScale = 1.0;
	private double _lastBaseX, _lastBaseY;

	[ObservableProperty] private bool isDirty;
	[ObservableProperty] private string activeTargetName = "Routing to: Local";

	// Drag
	public bool IsDragging { get; private set; }
	private string? _dragPeerId;
	private double _dragStartMouseX, _dragStartMouseY;
	private double _dragStartDraftX, _dragStartDraftY;
	private double _dragScale = 1.0;

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
		LoadPositionsFromStore();

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
			foreach (var t in DisplayTiles) t.IsActiveTarget = t.PeerId == peerId;
			foreach (var g in PeerGroups) g.IsActiveTarget = g.PeerId == peerId;
			var name = PeerGroups.FirstOrDefault(g => g.IsActiveTarget)?.PeerName
				?? (peerId == _localPeerId ? "Local" : peerId[..Math.Min(8, peerId.Length)]);
			ActiveTargetName = $"Routing to: {name}";
		});
	}

	private void LoadPositionsFromStore()
	{
		_positions.Clear();
		foreach (var kv in _persisted.Peers)
		{
			if (!kv.Value.HasSavedPosition) continue;
			var p = kv.Value;
			_positions[kv.Key] = (p.AppliedOffsetX, p.AppliedOffsetY, p.AppliedOffsetX, p.AppliedOffsetY);
		}
	}

	private void RefreshLayout(LayoutSnapshot? snap)
	{
		// Update raw rects cache
		_rawRects.Clear();
		if (snap != null)
		{
			foreach (var rect in snap.Peers)
			{
				if (rect.PeerId != _localPeerId)
					_rawRects[rect.PeerId] = rect;
			}
		}

		// Initialize positions for new remote peers
		foreach (var kv in _rawRects)
		{
			if (!_positions.ContainsKey(kv.Key))
			{
				var r = kv.Value;
				_positions[kv.Key] = (r.X, r.Y, r.X, r.Y);
			}
		}

		// Build virtual tile list: (peerId, peerName, isLocal, dispNum, isPrimary, stableId, vx, vy, vw, vh)
		var vTiles = new List<(string peerId, string peerName, bool isLocal, int dispNum,
			bool isPrimary, string stableId, double vx, double vy, double vw, double vh)>();

		// Local peer -- individual physical displays
		var localCluster = _displayService.GetLocalCluster();
		foreach (var d in localCluster.Displays)
		{
			vTiles.Add((_localPeerId, localCluster.PeerName, true, d.DisplayNumber, d.IsPrimary,
				d.StableId, d.X, d.Y, d.Width, d.Height));
		}

		// Remote peers -- one tile per peer using draft position
		foreach (var kv in _rawRects)
		{
			var rect = kv.Value;
			var pos = _positions[rect.PeerId];
			var name = string.IsNullOrEmpty(rect.DeviceName)
				? rect.PeerId[..Math.Min(8, rect.PeerId.Length)]
				: rect.DeviceName;
			vTiles.Add((rect.PeerId, name, false, 1, true, "remote",
				pos.dx, pos.dy, rect.Width, rect.Height));
		}

		if (vTiles.Count == 0)
		{
			DisplayTiles.Clear();
			PeerGroups.Clear();
			RefreshDirtyState();
			return;
		}

		// Global virtual bounds
		var minX = vTiles.Min(t => t.vx);
		var minY = vTiles.Min(t => t.vy);
		var maxX = vTiles.Max(t => t.vx + t.vw);
		var maxY = vTiles.Max(t => t.vy + t.vh);
		var totalW = Math.Max(1.0, maxX - minX);
		var totalH = Math.Max(1.0, maxY - minY);

		// Scale to fit canvas
		const double pad = 24;
		var usableW = Math.Max(1.0, CanvasWidth - 2 * pad);
		var usableH = Math.Max(1.0, CanvasHeight - 2 * pad);
		var scale = Math.Min(usableW / totalW, usableH / totalH);
		scale = Math.Max(0.02, Math.Min(scale, 0.5));

		var normW = totalW * scale;
		var normH = totalH * scale;
		var baseX = (CanvasWidth - normW) / 2.0;
		var baseY = (CanvasHeight - normH) / 2.0;

		_lastMinX = minX; _lastMinY = minY;
		_lastScale = scale;
		_lastBaseX = baseX; _lastBaseY = baseY;

		// Build tile VMs
		var newTiles = vTiles.Select(t => new DisplayTileVm(
			t.peerId, t.peerName, t.dispNum, t.isLocal, t.isPrimary, t.stableId,
			t.vx, t.vy, t.vw, t.vh)
		{
			Nx = baseX + (t.vx - minX) * scale,
			Ny = baseY + (t.vy - minY) * scale,
			Nw = Math.Max(32, t.vw * scale),
			Nh = Math.Max(20, t.vh * scale),
			IsActiveTarget = t.peerId == _routing.ActiveTargetPeerId
		}).ToList();

		// Build peer group VMs
		var newGroups = vTiles.GroupBy(t => t.peerId).Select(g =>
		{
			var first = g.First();
			var gMinX = g.Min(t => t.vx);
			var gMinY = g.Min(t => t.vy);
			var gMaxX = g.Max(t => t.vx + t.vw);
			var gMaxY = g.Max(t => t.vy + t.vh);

			var gNx = baseX + (gMinX - minX) * scale;
			var gNy = baseY + (gMinY - minY) * scale;
			var gNw = Math.Max(60, (gMaxX - gMinX) * scale);
			var gNh = (gMaxY - gMinY) * scale;

			return new PeerGroupVm(first.peerId, first.peerName, first.isLocal)
			{
				LabelX = gNx,
				LabelY = gNy + gNh + 5,
				LabelWidth = gNw,
				IsActiveTarget = first.peerId == _routing.ActiveTargetPeerId
			};
		}).ToList();

		DisplayTiles.Clear();
		foreach (var t in newTiles) DisplayTiles.Add(t);
		PeerGroups.Clear();
		foreach (var g in newGroups) PeerGroups.Add(g);

		var activeName = PeerGroups.FirstOrDefault(g => g.IsActiveTarget)?.PeerName ?? "Local";
		ActiveTargetName = $"Routing to: {activeName}";

		RefreshDirtyState();
	}

	// ---- Drag ----

	public void BeginDrag(string peerId, double mouseX, double mouseY)
	{
		if (peerId == _localPeerId) return;
		IsDragging = true;
		_dragPeerId = peerId;
		_dragStartMouseX = mouseX;
		_dragStartMouseY = mouseY;
		_dragScale = Math.Max(0.0001, _lastScale);

		var pos = _positions.TryGetValue(peerId, out var v) ? v : default;
		_dragStartDraftX = pos.dx;
		_dragStartDraftY = pos.dy;
	}

	public void DragTo(double mouseX, double mouseY)
	{
		if (!IsDragging || string.IsNullOrWhiteSpace(_dragPeerId)) return;

		var dxCanvas = mouseX - _dragStartMouseX;
		var dyCanvas = mouseY - _dragStartMouseY;
		var newDraftX = _dragStartDraftX + dxCanvas / _dragScale;
		var newDraftY = _dragStartDraftY + dyCanvas / _dragScale;

		if (_positions.TryGetValue(_dragPeerId, out var cur))
			_positions[_dragPeerId] = (cur.ax, cur.ay, newDraftX, newDraftY);
		else
			_positions[_dragPeerId] = (newDraftX, newDraftY, newDraftX, newDraftY);

		RefreshLayout(_layout.Current);
	}

	public void EndDrag()
	{
		IsDragging = false;
		_dragPeerId = null;
	}

	// ---- Commands ----

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
			peerState.AppliedOffsetX = v.dx;
			peerState.AppliedOffsetY = v.dy;
			peerState.HasSavedPosition = true;

			// Update routing engine so auto-switch works immediately
			if (peerId != _localPeerId && _rawRects.TryGetValue(peerId, out var rawRect))
			{
				_layout.UpsertPeerRect(new PeerRect(
					PeerId: peerId,
					DeviceName: rawRect.DeviceName,
					X: v.dx,
					Y: v.dy,
					Width: rawRect.Width,
					Height: rawRect.Height
				));
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

// ---- Tile VM ----

public sealed partial class DisplayTileVm : ObservableObject
{
	public string PeerId { get; }
	public string PeerName { get; }
	public int DisplayNumber { get; }
	public bool IsLocal { get; }
	public bool IsPrimary { get; }
	public string StableId { get; }
	public double VirtualX { get; }
	public double VirtualY { get; }
	public double VirtualW { get; }
	public double VirtualH { get; }

	[ObservableProperty] private double nx;
	[ObservableProperty] private double ny;
	[ObservableProperty] private double nw;
	[ObservableProperty] private double nh;
	[ObservableProperty] private bool isActiveTarget;

	public DisplayTileVm(string peerId, string peerName, int displayNumber, bool isLocal, bool isPrimary,
		string stableId, double virtualX, double virtualY, double virtualW, double virtualH)
	{
		PeerId = peerId; PeerName = peerName; DisplayNumber = displayNumber;
		IsLocal = isLocal; IsPrimary = isPrimary; StableId = stableId;
		VirtualX = virtualX; VirtualY = virtualY; VirtualW = virtualW; VirtualH = virtualH;
	}
}

// ---- Group label VM ----

public sealed partial class PeerGroupVm : ObservableObject
{
	public string PeerId { get; }
	public string PeerName { get; }
	public bool IsLocal { get; }

	[ObservableProperty] private double labelX;
	[ObservableProperty] private double labelY;
	[ObservableProperty] private double labelWidth;
	[ObservableProperty] private bool isActiveTarget;

	public PeerGroupVm(string peerId, string peerName, bool isLocal)
	{
		PeerId = peerId; PeerName = peerName; IsLocal = isLocal;
	}
}
