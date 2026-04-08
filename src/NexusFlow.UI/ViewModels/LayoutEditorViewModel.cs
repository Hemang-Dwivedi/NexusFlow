using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NexusFlow.Core.Layout;
using NexusFlow.Core.Routing;
using NexusFlow.Core.Services;
using NexusFlow.Identity;
using NexusFlow.Protocol.Control;
using NexusFlow.Settings.Layout;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NexusFlow.UI.ViewModels;

/// <summary>
/// Windows-Display-Settings-style layout editor.
///
/// ALL tiles (local and remote) are draggable. Tiles snap to each other's
/// edges within 14 px. On Apply, remote peer positions are pushed to the
/// routing engine AND a LayoutPositionSyncV1 message is sent so the remote
/// peer can update its own routing state symmetrically.
/// </summary>
public partial class LayoutEditorViewModel : ObservableObject, IDisposable
{
    private readonly ILayoutState _layout;
    private readonly ILayoutStore _layoutStore;
    private readonly IRoutingEngine _routing;
    private readonly IControlBroadcaster _broadcaster;
    private readonly DisplayService _displayService;
    private readonly string _localPeerId;
    private NexusFlow.Settings.Layout.LayoutState _persisted;

    public ObservableCollection<DisplayTileVm> DisplayTiles { get; } = new();
    public ObservableCollection<PeerGroupVm> PeerGroups { get; } = new();

    public double CanvasWidth  { get; } = 900;
    public double CanvasHeight { get; } = 400;
    public double WorkspaceWidth  => CanvasWidth;
    public double WorkspaceHeight => CanvasHeight;

    // Unified position dict.
    // Key = stableId for local display tiles, peerId for remote peer tiles.
    // Value = (applied_x, applied_y, draft_x, draft_y, tile_w, tile_h)
    private readonly Dictionary<string, (double Ax, double Ay, double Dx, double Dy, double Nw, double Nh)>
        _pos = new();

    // Raw virtual rects for remote peers (for routing / sync)
    private readonly Dictionary<string, PeerRect> _rawRects = new();

    // StableIds belonging to local display tiles (set per RefreshLayout call)
    private readonly HashSet<string> _localKeys = new();

    // Computed in RefreshLayout — stable regardless of tile dragging
    private double _localScale   = 1.0;
    private double _localMinX, _localMinY;
    private double _defaultOriginX = 24;   // default row left (used as Apply anchor)
    private double _defaultOriginY = 80;   // default row top  (used as Apply Y anchor)
    private double _rowH;

    [ObservableProperty] private bool isDirty;
    [ObservableProperty] private string activeTargetName = "Routing to: Local";

    // Drag state
    public bool IsDragging { get; private set; }
    private string? _dragKey;
    private double _dragStartMouseX, _dragStartMouseY;
    private double _dragStartDx, _dragStartDy;
    private double _dragNw, _dragNh;

    // Block-drag: when dragging any local tile in a multi-monitor setup,
    // ALL local tiles move together. We store their start positions here.
    private bool _isBlockDrag;
    private readonly Dictionary<string, (double Dx, double Dy)> _blockStartPos = new();

    public LayoutEditorViewModel(
        ILayoutState layout,
        ILayoutStore layoutStore,
        IRoutingEngine routing,
        ILocalIdentity me,
        DisplayService displayService,
        IControlBroadcaster broadcaster)
    {
        _layout      = layout;
        _layoutStore = layoutStore;
        _routing     = routing;
        _broadcaster = broadcaster;
        _localPeerId = me.PeerId;
        _displayService = displayService;

        _persisted = _layoutStore.Load();

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
            foreach (var g in PeerGroups)   g.IsActiveTarget = g.PeerId == peerId;
            var name = PeerGroups.FirstOrDefault(g => g.IsActiveTarget)?.PeerName
                ?? (peerId == _localPeerId ? "Local" : peerId[..Math.Min(8, peerId.Length)]);
            ActiveTargetName = $"Routing to: {name}";
        });
    }

    // ── Layout refresh ────────────────────────────────────────────────────────

    private void RefreshLayout(LayoutSnapshot? snap)
    {
        // 1. Collect remote peer rects
        _rawRects.Clear();
        if (snap != null)
            foreach (var r in snap.Peers)
                if (r.PeerId != _localPeerId)
                    _rawRects[r.PeerId] = r;

        // 2. Get local displays
        var localCluster = _displayService.GetLocalCluster();
        if (localCluster.Displays.Count == 0)
        {
            DisplayTiles.Clear();
            PeerGroups.Clear();
            RefreshDirtyState();
            return;
        }

        // 3. Compute uniform scale for local display row
        _localMinX = localCluster.Displays.Min(d => (double)d.X);
        _localMinY = localCluster.Displays.Min(d => (double)d.Y);

        var sorted = localCluster.Displays.OrderBy(d => d.X).ThenBy(d => d.Y).ToList();

        const double maxRowH  = 240.0;
        const double maxRowW  = 440.0;
        const double tileGap  = 10.0;
        const double minTileW = 80.0;
        const double minTileH = 52.0;

        double totalVW  = sorted.Sum(d => (double)d.Width);
        double maxVH    = sorted.Max(d => (double)d.Height);
        double scaleH   = maxRowH / maxVH;
        double gapTotal = tileGap * Math.Max(0, sorted.Count - 1);
        double scaleW   = (maxRowW - gapTotal) / totalVW;
        _localScale = Math.Max(0.01, Math.Min(scaleH, scaleW));
        _rowH = maxVH * _localScale;

        _defaultOriginX = 24;
        _defaultOriginY = (CanvasHeight - _rowH) / 2.0;

        // 4. Initialise / update local tile positions
        _localKeys.Clear();
        double cx = _defaultOriginX;
        foreach (var d in sorted)
        {
            double nw = Math.Max(minTileW, d.Width  * _localScale);
            double nh = Math.Max(minTileH, d.Height * _localScale);
            double ny = _defaultOriginY + (_rowH - nh) / 2.0;

            _localKeys.Add(d.StableId);

            if (!_pos.ContainsKey(d.StableId))
            {
                // First appearance: place in default row
                _pos[d.StableId] = (cx, ny, cx, ny, nw, nh);
            }
            else
            {
                // Keep dragged position; only update size (scale may change)
                var e = _pos[d.StableId];
                _pos[d.StableId] = (e.Ax, e.Ay, e.Dx, e.Dy, nw, nh);
            }
            cx += nw + tileGap;
        }

        // Right edge of default local row (used as default remote placement)
        double defaultLocalRight = cx - tileGap;

        // Actual right edge from current (possibly dragged) positions
        double actualLocalRight = _localKeys.Where(_pos.ContainsKey)
            .Select(k => _pos[k].Dx + _pos[k].Nw)
            .DefaultIfEmpty(defaultLocalRight).Max();

        // Actual LEFT edge of local cluster on canvas (anchor for relative remote placement)
        double localCanvasLeft = _localKeys.Where(_pos.ContainsKey)
            .Select(k => _pos[k].Dx)
            .DefaultIfEmpty(_defaultOriginX).Min();

        // Virtual extents of the local cluster (used to detect unpositioned remote peers)
        double localVirtMaxX = sorted.Max(d => (double)(d.X + d.Width));
        double localVirtMaxY = sorted.Max(d => (double)(d.Y + d.Height));

        // 5. Prune stale remote keys from _pos
        var validRemote = _rawRects.Keys.ToHashSet();
        foreach (var k in _pos.Keys.Except(_localKeys).Except(validRemote).ToList())
            _pos.Remove(k);

        // 6. Initialise / update remote tile positions.
        //
        // Canvas position is derived RELATIVE to where the local cluster sits on
        // canvas — so the layout is correct regardless of canvas size or resolution.
        //
        // Exception: peers whose virtual rect still overlaps the local cluster have
        // not been positioned yet (both start at 0,0). Those get a default placement
        // to the right so tiles never stack on top of each other at startup.
        //
        // If the user is actively dragging a tile or has an uncommitted local change
        // (Dx != Ax), the in-progress edit is preserved so the drag is not interrupted.
        double remoteAreaW = CanvasWidth  - actualLocalRight - 30;
        double remoteAreaH = CanvasHeight - 20;

        foreach (var kv in _rawRects)
        {
            var rect = kv.Value;
            double rScaleW = remoteAreaW / Math.Max(1, rect.Width);
            double rScaleH = remoteAreaH / Math.Max(1, rect.Height);
            double rScale  = Math.Min(_localScale, Math.Min(rScaleW, rScaleH));
            rScale = Math.Max(rScale, 0.01);
            double rNw = Math.Max(minTileW, rect.Width  * rScale);
            double rNh = Math.Max(minTileH, rect.Height * rScale);

            // Relative canvas position: offset from the local cluster's actual canvas
            // left by the virtual offset between remote and local.
            double derivedX = localCanvasLeft + (rect.X - _localMinX) * _localScale;
            double derivedY = _defaultOriginY  + (rect.Y - _localMinY) * _localScale;

            // Only fall back to right-of-local placement for unpositioned peers —
            // those whose virtual rect overlaps the local cluster (not yet arranged).
            bool isUnpositioned =
                rect.X < localVirtMaxX && rect.X + rect.Width  > _localMinX &&
                rect.Y < localVirtMaxY && rect.Y + rect.Height > _localMinY;
            if (isUnpositioned)
                derivedX = actualLocalRight + 20;

            derivedX = Math.Max(0, Math.Min(CanvasWidth  - rNw, derivedX));
            derivedY = Math.Max(0, Math.Min(CanvasHeight - rNh, derivedY));

            if (!_pos.ContainsKey(rect.PeerId))
            {
                _pos[rect.PeerId] = (derivedX, derivedY, derivedX, derivedY, rNw, rNh);
            }
            else
            {
                var e = _pos[rect.PeerId];
                bool pending   = Math.Abs(e.Dx - e.Ax) > 0.5 || Math.Abs(e.Dy - e.Ay) > 0.5;
                bool dragging  = rect.PeerId == _dragKey;

                if (pending || dragging)
                    // Local edit in progress — keep draft, only refresh size
                    _pos[rect.PeerId] = (e.Ax, e.Ay, e.Dx, e.Dy, rNw, rNh);
                else
                    // No pending changes — sync canvas position from routing state
                    _pos[rect.PeerId] = (derivedX, derivedY, derivedX, derivedY, rNw, rNh);
            }
        }

        // 7. Build tile VMs
        var newTiles = new List<DisplayTileVm>();

        // All local tiles are draggable. When there are multiple displays the
        // whole block moves together (block-drag), so every tile acts as a
        // handle for the group.
        const bool localTilesDraggable = true;

        foreach (var d in sorted)
        {
            var p = _pos[d.StableId];
            newTiles.Add(new DisplayTileVm(
                _localPeerId, localCluster.PeerName,
                d.DisplayNumber, isLocal: true, d.IsPrimary, d.StableId,
                d.X, d.Y, d.Width, d.Height)
            {
                Nx = p.Dx, Ny = p.Dy, Nw = p.Nw, Nh = p.Nh,
                IsDraggable    = localTilesDraggable,
                IsActiveTarget = _localPeerId == _routing.ActiveTargetPeerId
            });
        }

        foreach (var kv in _rawRects)
        {
            var rect = kv.Value;
            var p    = _pos[rect.PeerId];
            var name = string.IsNullOrEmpty(rect.DeviceName)
                ? rect.PeerId[..Math.Min(8, rect.PeerId.Length)]
                : rect.DeviceName;
            newTiles.Add(new DisplayTileVm(
                rect.PeerId, name, 1, isLocal: false, isPrimary: true, "remote",
                rect.X, rect.Y, rect.Width, rect.Height)
            {
                Nx = p.Dx, Ny = p.Dy, Nw = p.Nw, Nh = p.Nh,
                IsDraggable    = true,
                IsActiveTarget = rect.PeerId == _routing.ActiveTargetPeerId
            });
        }

        // 8. Build peer group labels
        var newGroups = new List<PeerGroupVm>();

        double localLabelLeft  = _localKeys.Where(_pos.ContainsKey).Select(k => _pos[k].Dx).DefaultIfEmpty(_defaultOriginX).Min();
        double localLabelRight = _localKeys.Where(_pos.ContainsKey).Select(k => _pos[k].Dx + _pos[k].Nw).DefaultIfEmpty(defaultLocalRight).Max();

        newGroups.Add(new PeerGroupVm(_localPeerId, localCluster.PeerName, isLocal: true)
        {
            LabelX     = localLabelLeft,
            LabelY     = _defaultOriginY + _rowH + 6,
            LabelWidth = Math.Max(60, localLabelRight - localLabelLeft),
            IsActiveTarget = _localPeerId == _routing.ActiveTargetPeerId
        });

        foreach (var kv in _rawRects)
        {
            var rect = kv.Value;
            if (!_pos.TryGetValue(rect.PeerId, out var p)) continue;
            var name = string.IsNullOrEmpty(rect.DeviceName)
                ? rect.PeerId[..Math.Min(8, rect.PeerId.Length)]
                : rect.DeviceName;
            newGroups.Add(new PeerGroupVm(rect.PeerId, name, isLocal: false)
            {
                LabelX     = p.Dx,
                LabelY     = p.Dy + p.Nh + 6,
                LabelWidth = Math.Max(60, p.Nw),
                IsActiveTarget = rect.PeerId == _routing.ActiveTargetPeerId
            });
        }

        // 9. Commit to UI
        DisplayTiles.Clear();
        foreach (var t in newTiles) DisplayTiles.Add(t);
        PeerGroups.Clear();
        foreach (var g in newGroups) PeerGroups.Add(g);

        var activeName = PeerGroups.FirstOrDefault(g => g.IsActiveTarget)?.PeerName ?? "Local";
        ActiveTargetName = $"Routing to: {activeName}";

        RefreshDirtyState();
    }

    // ── Drag ─────────────────────────────────────────────────────────────────

    public void BeginDrag(DisplayTileVm tile, double mouseX, double mouseY)
    {
        if (!tile.IsDraggable) return;
        var key = tile.IsLocal ? tile.StableId : tile.PeerId;
        if (!_pos.TryGetValue(key, out var p)) return;

        IsDragging       = true;
        _dragKey         = key;
        _dragStartMouseX = mouseX;
        _dragStartMouseY = mouseY;
        _dragStartDx     = p.Dx;
        _dragStartDy     = p.Dy;
        _dragNw          = p.Nw;
        _dragNh          = p.Nh;

        // Block drag: multiple local displays move as one unit
        _isBlockDrag = tile.IsLocal && _localKeys.Count > 1;
        _blockStartPos.Clear();
        if (_isBlockDrag)
            foreach (var lk in _localKeys)
                if (_pos.TryGetValue(lk, out var lp))
                    _blockStartPos[lk] = (lp.Dx, lp.Dy);
    }

    public void DragTo(double mouseX, double mouseY)
    {
        if (!IsDragging || string.IsNullOrWhiteSpace(_dragKey)) return;

        double dxMouse = mouseX - _dragStartMouseX;
        double dyMouse = mouseY - _dragStartMouseY;

        // ── Block drag (multi-monitor local group) ────────────────────────────
        if (_isBlockDrag)
        {
            // Clamp the delta so that no tile in the block exits the canvas
            double minDx = double.MinValue, maxDx = double.MaxValue;
            double minDy = double.MinValue, maxDy = double.MaxValue;
            foreach (var kv in _blockStartPos)
            {
                if (!_pos.TryGetValue(kv.Key, out var lp)) continue;
                minDx = Math.Max(minDx, -kv.Value.Dx);
                maxDx = Math.Min(maxDx, CanvasWidth  - lp.Nw - kv.Value.Dx);
                minDy = Math.Max(minDy, -kv.Value.Dy);
                maxDy = Math.Min(maxDy, CanvasHeight - lp.Nh - kv.Value.Dy);
            }
            double cdx = Math.Clamp(dxMouse, minDx, maxDx);
            double cdy = Math.Clamp(dyMouse, minDy, maxDy);

            // Snap the dragged tile's tentative position against remote tiles only
            double tentDx = _dragStartDx + cdx;
            double tentDy = _dragStartDy + cdy;
            const double snapThreshold = 14.0;
            double xSnap = tentDx, xBest = snapThreshold;
            double ySnap = tentDy, yBest = snapThreshold;

            foreach (var kv in _pos)
            {
                if (_localKeys.Contains(kv.Key)) continue;  // remote tiles only
                var o = kv.Value;
                double oL = o.Dx, oR = o.Dx + o.Nw, oT = o.Dy, oB = o.Dy + o.Nh;
                double nR = tentDx + _dragNw, nB = tentDy + _dragNh;

                (double d, double t)[] xCands =
                [
                    (Math.Abs(tentDx - oR), oR),
                    (Math.Abs(nR     - oL), oL - _dragNw),
                    (Math.Abs(tentDx - oL), oL),
                    (Math.Abs(nR     - oR), oR - _dragNw),
                ];
                foreach (var (dist, target) in xCands)
                    if (dist < xBest) { xBest = dist; xSnap = target; }

                (double d, double t)[] yCands =
                [
                    (Math.Abs(tentDy - oB), oB),
                    (Math.Abs(nB     - oT), oT - _dragNh),
                    (Math.Abs(tentDy - oT), oT),
                    (Math.Abs(nB     - oB), oB - _dragNh),
                ];
                foreach (var (dist, target) in yCands)
                    if (dist < yBest) { yBest = dist; ySnap = target; }
            }

            // Apply the snapped delta uniformly to every tile in the block
            double finalDx = xSnap - _dragStartDx;
            double finalDy = ySnap - _dragStartDy;
            foreach (var kv in _blockStartPos)
            {
                if (!_pos.TryGetValue(kv.Key, out var cur)) continue;
                _pos[kv.Key] = (cur.Ax, cur.Ay,
                    kv.Value.Dx + finalDx,
                    kv.Value.Dy + finalDy,
                    cur.Nw, cur.Nh);
            }

            RefreshLayout(_layout.Current);
            return;
        }

        // ── Single-tile drag (remote peers, or single-monitor local) ──────────
        double newDx = _dragStartDx + dxMouse;
        double newDy = _dragStartDy + dyMouse;
        newDx = Math.Max(0, Math.Min(CanvasWidth  - _dragNw, newDx));
        newDy = Math.Max(0, Math.Min(CanvasHeight - _dragNh, newDy));

        const double snap = 14.0;
        double xS = newDx, xB = snap;
        double yS = newDy, yB = snap;

        foreach (var kv in _pos)
        {
            if (kv.Key == _dragKey) continue;
            var o = kv.Value;
            double oL = o.Dx, oR = o.Dx + o.Nw, oT = o.Dy, oB = o.Dy + o.Nh;
            double nR = newDx + _dragNw, nB = newDy + _dragNh;

            (double d, double t)[] xCands =
            [
                (Math.Abs(newDx - oR), oR),
                (Math.Abs(nR    - oL), oL - _dragNw),
                (Math.Abs(newDx - oL), oL),
                (Math.Abs(nR    - oR), oR - _dragNw),
            ];
            foreach (var (dist, target) in xCands)
                if (dist < xB) { xB = dist; xS = target; }

            (double d, double t)[] yCands =
            [
                (Math.Abs(newDy - oB), oB),
                (Math.Abs(nB    - oT), oT - _dragNh),
                (Math.Abs(newDy - oT), oT),
                (Math.Abs(nB    - oB), oB - _dragNh),
            ];
            foreach (var (dist, target) in yCands)
                if (dist < yB) { yB = dist; yS = target; }
        }

        if (_pos.TryGetValue(_dragKey, out var cur2))
            _pos[_dragKey] = (cur2.Ax, cur2.Ay, xS, yS, cur2.Nw, cur2.Nh);

        RefreshLayout(_layout.Current);
    }

    public void EndDrag()
    {
        IsDragging = false;
        _dragKey   = null;
    }

    // ── Commands ──────────────────────────────────────────────────────────────

    private void RefreshDirtyState()
    {
        IsDirty = _pos.Values.Any(v =>
            Math.Abs(v.Dx - v.Ax) > 0.5 ||
            Math.Abs(v.Dy - v.Ay) > 0.5);
        ApplyCommand.NotifyCanExecuteChanged();
        RevertCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanApply))]
    private void Apply()
    {
        // Use the top-left corner of the local display cluster as the canvas→virtual
        // anchor.  This correctly accounts for the block having been dragged.
        double anchorCanvasX = _localKeys.Where(_pos.ContainsKey)
            .Select(k => _pos[k].Dx)
            .DefaultIfEmpty(_defaultOriginX)
            .Min();
        double anchorCanvasY = _localKeys.Where(_pos.ContainsKey)
            .Select(k => _pos[k].Dy)
            .DefaultIfEmpty(_defaultOriginY)
            .Min();

        foreach (var key in _pos.Keys.ToList())
        {
            var v = _pos[key];
            // Mark as applied
            _pos[key] = (v.Dx, v.Dy, v.Dx, v.Dy, v.Nw, v.Nh);

            // Remote peers only: update routing + sync to peer
            if (_localKeys.Contains(key)) continue;
            if (!_rawRects.TryGetValue(key, out var rawRect)) continue;

            double virtualX = _localMinX + (v.Dx - anchorCanvasX) / Math.Max(0.0001, _localScale);
            double virtualY = _localMinY + (v.Dy - anchorCanvasY) / Math.Max(0.0001, _localScale);

            // Persist
            if (!_persisted.Peers.TryGetValue(key, out var peerState))
            {
                peerState = new PeerLayoutState();
                _persisted.Peers[key] = peerState;
            }
            peerState.AppliedOffsetX  = virtualX;
            peerState.AppliedOffsetY  = virtualY;
            peerState.HasSavedPosition = true;

            // Push to routing engine
            _layout.UpsertPeerRect(new PeerRect(
                PeerId:     key,
                DeviceName: rawRect.DeviceName,
                X:          virtualX,
                Y:          virtualY,
                Width:      rawRect.Width,
                Height:     rawRect.Height));

            // Sync to remote peer: tell them where WE placed them
            double relDx = virtualX - _localMinX;
            double relDy = virtualY - _localMinY;
            var capturedKey = key;
            _ = Task.Run(async () =>
            {
                try
                {
                    await _broadcaster.SendToPeerAsync(
                        capturedKey,
                        new LayoutPositionSyncV1(_localPeerId, capturedKey, relDx, relDy),
                        CancellationToken.None).ConfigureAwait(false);
                }
                catch { /* peer may be offline */ }
            });
        }

        _layoutStore.Save(_persisted);
        RefreshDirtyState();
    }

    private bool CanApply() => IsDirty;

    [RelayCommand(CanExecute = nameof(CanRevert))]
    private void Revert()
    {
        foreach (var key in _pos.Keys.ToList())
        {
            var v = _pos[key];
            _pos[key] = (v.Ax, v.Ay, v.Ax, v.Ay, v.Nw, v.Nh);
        }
        RefreshLayout(_layout.Current);
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

    public bool IsDraggable { get; init; }

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
