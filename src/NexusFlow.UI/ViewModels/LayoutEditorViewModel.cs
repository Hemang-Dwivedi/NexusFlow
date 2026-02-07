using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NexusFlow.Core.Layout;
using NexusFlow.Settings.Layout;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace NexusFlow.UI.ViewModels;

/// <summary>
/// Layout editor that renders ALL peers known to the runtime layout state.
/// For now peers are drawn as 1 rectangle each (desktop bounds).
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
	private readonly Dictionary<string, (double ax, double ay, double dx, double dy)> _offsets = new();

	[ObservableProperty] private bool isDirty;

	// drag
	public bool IsDragging { get; private set; }
	private string? _dragPeerId;
	private double _dragStartMouseX, _dragStartMouseY;
	private double _dragStartDraftX, _dragStartDraftY;

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

		var peers = snap?.Peers?.ToList() ?? new List<PeerRect>();
		if (peers.Count == 0)
		{
			RefreshDirtyState();
			return;
		}

		// Ensure offset entries exist for all peers
		foreach (var p in peers)
			_ = GetOrCreateOffsets(p.PeerId);

		// Build global bounds (real pixels + applied offsets) so all peers fit proportionally
		var bounds = peers.Select(p =>
		{
			var off = GetOrCreateOffsets(p.PeerId);
			var ox = off.dx; // use draft for preview
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

		foreach (var p in peers.OrderBy(p => p.PeerId))
		{
			var off = GetOrCreateOffsets(p.PeerId);
			var x = (p.X + off.dx) - minX;
			var y = (p.Y + off.dy) - minY;

			PeerBlocks.Add(new PeerBlockVm(
				peerId: p.PeerId,
				nx: baseX + x * scale,
				ny: baseY + y * scale,
				nw: Math.Max(18, p.Width * scale),
				nh: Math.Max(18, p.Height * scale)
			));
		}

		RefreshDirtyState();
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

		// Drag works in canvas pixels; offsets are in "real pixel space".
		// We don't have the exact inverse scale here (because scale depends on all peers),
		// so we use a simple approximation: treat draft offsets as canvas-space offsets.
		// This is enough to unblock UI; later we can store offsets in virtual px precisely.
		var dx = mouseX - _dragStartMouseX;
		var dy = mouseY - _dragStartMouseY;

		SetDraft(_dragPeerId, _dragStartDraftX + dx, _dragStartDraftY + dy);

		// Re-render with new draft offsets
		RefreshFromSnapshot(_layout.Current);
	}

	public void EndDrag()
	{
		IsDragging = false;
		_dragPeerId = null;
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

	[ObservableProperty] private double nx;
	[ObservableProperty] private double ny;
	[ObservableProperty] private double nw;
	[ObservableProperty] private double nh;

	public PeerBlockVm(string peerId, double nx, double ny, double nw, double nh)
	{
		PeerId = peerId;
		Nx = nx; Ny = ny; Nw = nw; Nh = nh;
	}
}
