using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NexusFlow.Core.Layout;
using NexusFlow.Settings.Layout;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using NexusFlow.Display.Layout;
using NexusFlow.Display.Models;

namespace NexusFlow.UI.ViewModels;

/// <summary>
/// Layout editor that renders ALL peers known to the runtime layout state.
/// Peers are drawn as 1 rectangle each (desktop bounds).
/// Offsets are editable (Draft) and persisted to ILayoutStore.
/// </summary>
public partial class LayoutEditorViewModel : ObservableObject, IDisposable
{
	private readonly ILayoutState _layout;
	private readonly ILayoutStore _layoutStore;
	private NexusFlow.Settings.Layout.LayoutState _persisted;

	public ObservableCollection<PeerBlockVm> PeerBlocks { get; } = new();

	public double CanvasWidth { get; } = 900;
	public double CanvasHeight { get; } = 400;

	// Draft/applied offsets per peerId (stored in LayoutStore)
	// ax/ay = applied, dx/dy = draft
	private readonly Dictionary<string, (double ax, double ay, double dx, double dy)> _offsets = new();

	// Last snapshot peer rects (virtual desktop px)
	private readonly Dictionary<string, PeerRect> _peerRects = new();

	// Last normalization parameters (so Drag can convert canvas <-> virtual precisely)
	private double _lastMinX, _lastMinY;
	private double _lastScale = 1.0;
	private double _lastBaseX, _lastBaseY;

	[ObservableProperty] private bool isDirty;

	// drag
	public bool IsDragging { get; private set; }
	private string? _dragPeerId;
	private double _dragStartMouseX, _dragStartMouseY;
	private double _dragStartDraftX, _dragStartDraftY;

	// ---- Non-overlap tuning (canvas-space) ----
	private const double PeerPadding = 10;        // minimum gap between peer blocks
	private const int ResolveMaxIterations = 10;  // keep small + stable

	private readonly struct RectD
	{
		public readonly double X, Y, W, H;
		public double Left => X;
		public double Top => Y;
		public double Right => X + W;
		public double Bottom => Y + H;

		public RectD(double x, double y, double w, double h)
		{
			X = x; Y = y; W = w; H = h;
		}
	}

	private static bool Intersects(in RectD a, in RectD b)
		=> a.Left < b.Right &&
		   a.Right > b.Left &&
		   a.Top < b.Bottom &&
		   a.Bottom > b.Top;

	private static (double ox, double oy) Overlap(in RectD a, in RectD b)
	{
		var ox = Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left);
		var oy = Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top);
		return (ox, oy);
	}

	public LayoutEditorViewModel(ILayoutState layout, ILayoutStore layoutStore)
	{
		_layout = layout;
		_layoutStore = layoutStore;

		_persisted = _layoutStore.Load();
		LoadOffsetsFromStore();

		_layout.Changed += OnLayoutChanged;

		// initial populate if already present
		RefreshFromSnapshot(_layout.Current);
	}

	public void Dispose()
	{
		_layout.Changed -= OnLayoutChanged;
	}

	private void OnLayoutChanged(LayoutSnapshot? snap)
	{
		Dispatcher.UIThread.Post(() => RefreshFromSnapshot(snap));
	}

	private void LoadOffsetsFromStore()
	{
		_offsets.Clear();
		foreach (var kv in _persisted.Peers)
		{
			var peerId = kv.Key;
			var st = kv.Value;

			var ax = st.AppliedOffsetX;
			var ay = st.AppliedOffsetY;
			_offsets[peerId] = (ax, ay, ax, ay);
		}
	}

	private (double ax, double ay, double dx, double dy) GetOrCreateOffsets(string peerId)
	{
		if (_offsets.TryGetValue(peerId, out var v))
			return v;

		_offsets[peerId] = (0, 0, 0, 0);
		return _offsets[peerId];
	}

	private void SetDraft(string peerId, double dx, double dy)
	{
		var v = GetOrCreateOffsets(peerId);
		_offsets[peerId] = (v.ax, v.ay, dx, dy);
		RefreshDirtyState();
	}

	private void RefreshDirtyState()
	{
		IsDirty = _offsets.Values.Any(v =>
			Math.Abs(v.dx - v.ax) > 0.01 ||
			Math.Abs(v.dy - v.ay) > 0.01);

		ApplyCommand.NotifyCanExecuteChanged();
		RevertCommand.NotifyCanExecuteChanged();
	}

	private void RefreshFromSnapshot(LayoutSnapshot? snap)
	{
		PeerBlocks.Clear();
		_peerRects.Clear();

		var peers = snap?.Peers?.ToList() ?? new List<PeerRect>();
		if (peers.Count == 0)
		{
			RefreshDirtyState();
			return;
		}

		foreach (var p in peers)
			_peerRects[p.PeerId] = p;

		// Ensure offset entries exist for all peers
		foreach (var p in peers)
			_ = GetOrCreateOffsets(p.PeerId);

		// Build global bounds (virtual px + draft offsets) so all peers fit proportionally
		var bounds = peers.Select(p =>
		{
			var off = GetOrCreateOffsets(p.PeerId);
			var ox = off.dx;
			var oy = off.dy;

			var x1 = p.X + ox;
			var y1 = p.Y + oy;
			var x2 = x1 + p.Width;
			var y2 = y1 + p.Height;
			return (p.PeerId, x1, y1, x2, y2);
		}).ToList();

		var minX = bounds.Min(b => b.x1);
		var minY = bounds.Min(b => b.y1);
		var maxX = bounds.Max(b => b.x2);
		var maxY = bounds.Max(b => b.y2);

		var totalW = Math.Max(1.0, maxX - minX);
		var totalH = Math.Max(1.0, maxY - minY);

		const double pad = 14;

		var usableW = Math.Max(1.0, CanvasWidth - 2 * pad);
		var usableH = Math.Max(1.0, CanvasHeight - 2 * pad);

		var scale = Math.Min(usableW / totalW, usableH / totalH);
		scale = Math.Min(scale, 1.0);
		scale = Math.Max(scale, 0.03);

		var normW = totalW * scale;
		var normH = totalH * scale;

		var baseX = (CanvasWidth - normW) / 2.0;
		var baseY = (CanvasHeight - normH) / 2.0;

		// Persist normalization params for drag conversion
		_lastMinX = minX;
		_lastMinY = minY;
		_lastScale = scale;
		_lastBaseX = baseX;
		_lastBaseY = baseY;

		foreach (var p in peers.OrderBy(p => p.PeerId))
		{
			var off = GetOrCreateOffsets(p.PeerId);

			var x = (p.X + off.dx) - minX;
			var y = (p.Y + off.dy) - minY;

			var nx = baseX + x * scale;
			var ny = baseY + y * scale;
			var nw = Math.Max(18, p.Width * scale);
			var nh = Math.Max(18, p.Height * scale);
			var peerName = p.PeerId; // placeholder until LayoutSnapshot includes names
			var normalized = BuildDesktopNormalized(p.PeerId, peerName, p);

			PeerBlocks.Add(new PeerBlockVm(
				peerId: p.PeerId,
				peerName: peerName,
				normalized: normalized,
				nx: nx,
				ny: ny,
				nw: nw,
				nh: nh
			));

		}

		RefreshDirtyState();
	}


	private static NormalizedCluster BuildDesktopNormalized(string peerId, string peerName, PeerRect r)
	{
		// Minimal: represent the peer as one big "desktop" display.
		var snap = new DisplaySnapshot(
			StableId: "desktop",
			DisplayNumber: 1,
			IsPrimary: true,
			X: r.X,
			Y: r.Y,
			Width: r.Width,
			Height: r.Height,
			RotationDegrees: 0,
			DpiX: 96,
			DpiY: 96
		);

		var cluster = new PeerDisplayCluster(peerId, peerName, new[] { snap });
		return DisplayLayoutNormalizer.Normalize(cluster, maxWidth: 240, maxHeight: 130, padding: 8);
	}

	// ---------------- Drag ----------------

	public void BeginDrag(string peerId, double mouseX, double mouseY)
	{
		IsDragging = true;
		_dragPeerId = peerId;
		_dragStartMouseX = mouseX;
		_dragStartMouseY = mouseY;

		var v = GetOrCreateOffsets(peerId);
		_dragStartDraftX = v.dx;
		_dragStartDraftY = v.dy;
	}

	public void DragTo(double mouseX, double mouseY)
	{
		if (!IsDragging || string.IsNullOrWhiteSpace(_dragPeerId))
			return;

		if (!_peerRects.TryGetValue(_dragPeerId, out var peerRect))
			return;

		// Convert canvas delta -> virtual delta using the last computed scale.
		// (This fixes the "drag feels wrong" issue and keeps offsets in real pixels.)
		var dxCanvas = mouseX - _dragStartMouseX;
		var dyCanvas = mouseY - _dragStartMouseY;

		var s = Math.Max(0.0001, _lastScale);
		var dxVirtual = dxCanvas / s;
		var dyVirtual = dyCanvas / s;

		var proposedDraftX = _dragStartDraftX + dxVirtual;
		var proposedDraftY = _dragStartDraftY + dyVirtual;

		// Compute proposed canvas position for this peer (top-left)
		var proposedNx = VirtualToCanvasX(peerRect.X + proposedDraftX);
		var proposedNy = VirtualToCanvasY(peerRect.Y + proposedDraftY);

		// Non-overlap resolution in canvas-space (stable + matches what user sees)
		(var resolvedNx, var resolvedNy) = ResolveNoOverlapCanvas(_dragPeerId, proposedNx, proposedNy);

		// Convert resolved canvas pos back to virtual draft offsets
		var resolvedVirtualX = CanvasToVirtualX(resolvedNx);
		var resolvedVirtualY = CanvasToVirtualY(resolvedNy);

		var finalDraftX = resolvedVirtualX - peerRect.X;
		var finalDraftY = resolvedVirtualY - peerRect.Y;

		SetDraft(_dragPeerId, finalDraftX, finalDraftY);

		// Re-render with new draft offsets
		RefreshFromSnapshot(_layout.Current);
	}

	public void EndDrag()
	{
		IsDragging = false;
		_dragPeerId = null;
	}

	private double VirtualToCanvasX(double virtualX)
		=> _lastBaseX + (virtualX - _lastMinX) * _lastScale;

	private double VirtualToCanvasY(double virtualY)
		=> _lastBaseY + (virtualY - _lastMinY) * _lastScale;

	private double CanvasToVirtualX(double canvasX)
		=> ((canvasX - _lastBaseX) / Math.Max(0.0001, _lastScale)) + _lastMinX;

	private double CanvasToVirtualY(double canvasY)
		=> ((canvasY - _lastBaseY) / Math.Max(0.0001, _lastScale)) + _lastMinY;

	private (double x, double y) ResolveNoOverlapCanvas(string movingPeerId, double proposedNx, double proposedNy)
	{
		// Find moving block size (from current rendered blocks)
		var movingVm = PeerBlocks.FirstOrDefault(p => p.PeerId == movingPeerId);
		if (movingVm is null)
			return (proposedNx, proposedNy);

		var x = proposedNx;
		var y = proposedNy;

		for (int iter = 0; iter < ResolveMaxIterations; iter++)
		{
			var me = new RectD(x, y, movingVm.Nw, movingVm.Nh);
			bool adjusted = false;

			foreach (var other in PeerBlocks)
			{
				if (other.PeerId == movingPeerId) continue;

				// Inflate other by padding to keep a gap
				var otherRect = new RectD(
					other.Nx - PeerPadding,
					other.Ny - PeerPadding,
					other.Nw + PeerPadding * 2,
					other.Nh + PeerPadding * 2);

				if (!Intersects(me, otherRect))
					continue;

				var (ox, oy) = Overlap(me, otherRect);
				if (ox <= 0 || oy <= 0)
					continue;

				// Push out along smaller overlap axis
				if (ox < oy)
					x += me.Left < otherRect.Left ? -ox : ox;
				else
					y += me.Top < otherRect.Top ? -oy : oy;

				adjusted = true;
				break; // re-evaluate after first collision resolution
			}

			if (!adjusted)
				break;
		}

		return (x, y);
	}

	// ---------------- Commands ----------------

	[RelayCommand(CanExecute = nameof(CanApply))]
	private void Apply()
	{
		foreach (var (peerId, v) in _offsets.ToList())
		{
			_offsets[peerId] = (v.dx, v.dy, v.dx, v.dy);

			if (!_persisted.Peers.TryGetValue(peerId, out var peer))
			{
				peer = new PeerLayoutState();
				_persisted.Peers[peerId] = peer;
			}

			peer.AppliedOffsetX = v.dx;
			peer.AppliedOffsetY = v.dy;
		}

		_layoutStore.Save(_persisted);
		RefreshDirtyState();
	}

	private bool CanApply() => IsDirty;

	[RelayCommand(CanExecute = nameof(CanRevert))]
	private void Revert()
	{
		foreach (var peerId in _offsets.Keys.ToList())
		{
			var v = _offsets[peerId];
			_offsets[peerId] = (v.ax, v.ay, v.ax, v.ay);
		}

		RefreshFromSnapshot(_layout.Current);
		RefreshDirtyState();
	}

	private bool CanRevert() => IsDirty;
}

public sealed partial class PeerBlockVm : ObservableObject
{
	public string PeerId { get; }
	public string PeerName { get; }
	public NormalizedCluster Normalized { get; }
	[ObservableProperty] private double nx;
	[ObservableProperty] private double ny;
	[ObservableProperty] private double nw;
	[ObservableProperty] private double nh;

	public PeerBlockVm(string peerId, string peerName, NormalizedCluster normalized, double nx, double ny, double nw, double nh)
	{
		PeerId = peerId;
		PeerName = peerName;
		Normalized = normalized;
		Nx = nx; Ny = ny; Nw = nw; Nh = nh;
	}
}
