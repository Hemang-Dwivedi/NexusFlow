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

        const double tileGap  = 10.0;
        const double minTileW = 80.0;
        const double minTileH = 52.0;
        const double padding  = 24.0;

        // Virtual extents of the local cluster
        double localVirtMaxX = sorted.Max(d => (double)(d.X + d.Width));
        double localVirtMaxY = sorted.Max(d => (double)(d.Y + d.Height));
        double localVirtW    = localVirtMaxX - _localMinX;
        double localVirtH    = localVirtMaxY - _localMinY;

        // Full virtual extent including all remote peers (for scale-to-fit-all).
        // Unpositioned peers (still at 0,0) are assumed adjacent to the local right edge.
        double fullVirtMinX = _localMinX, fullVirtMaxX = localVirtMaxX;
        double fullVirtMinY = _localMinY, fullVirtMaxY = localVirtMaxY;
        foreach (var r in _rawRects.Values)
        {
            bool unpos = r.X < localVirtMaxX && r.X + r.Width  > _localMinX &&
                         r.Y < localVirtMaxY && r.Y + r.Height > _localMinY;
            if (unpos)
                fullVirtMaxX = Math.Max(fullVirtMaxX, localVirtMaxX + r.Width);
            else
            {
                fullVirtMinX = Math.Min(fullVirtMinX, r.X);
                fullVirtMaxX = Math.Max(fullVirtMaxX, r.X + r.Width);
                fullVirtMinY = Math.Min(fullVirtMinY, r.Y);
                fullVirtMaxY = Math.Max(fullVirtMaxY, r.Y + r.Height);
            }
        }

        // Compute _localScale to fit ALL clusters inside the canvas at once
        double localGapTotal = tileGap * Math.Max(0, sorted.Count - 1);
        double scaleW = (CanvasWidth  - 2 * padding - localGapTotal) / Math.Max(1, fullVirtMaxX - fullVirtMinX);
        double scaleH = (CanvasHeight - 2 * padding) / Math.Max(1, fullVirtMaxY - fullVirtMinY);
        _localScale = Math.Max(0.01, Math.Min(scaleW, scaleH));
        _rowH = localVirtH * _localScale;

        // Compute _defaultOriginX: the canvas X where virtual offset 0 (local left edge) lives.
        //
        // All tiles — local AND remote — use _localScale, so:
        //   canvasX = _defaultOriginX + (virtualX - _localMinX) * _localScale
        //
        // We need _defaultOriginX large enough that even peers with NEGATIVE virtual offset
        // (i.e. positioned to the LEFT of the local cluster) get a non-negative canvas X.
        //
        // Algorithm: find the min/max signed virtual offsets of all positioned remote peers,
        // then pick _defaultOriginX so the whole arrangement is centred and has padding >= 24px.
        double minVirtOff = 0.0;        // local left = offset 0
        double maxVirtOff = localVirtW; // local right edge

        foreach (var kv in _rawRects)
        {
            var r = kv.Value;
            // "Unpositioned" = virtual rect overlaps local (both peers start at 0,0 — not yet arranged)
            bool isUnpos = r.X < localVirtMaxX && r.X + r.Width > _localMinX &&
                           r.Y < localVirtMaxY && r.Y + r.Height > _localMinY;
            if (isUnpos)
            {
                // Treat as adjacent to local right for centering purposes
                maxVirtOff = Math.Max(maxVirtOff, localVirtW + r.Width);
                continue;
            }

            double leftOff  = r.X - _localMinX;
            double rightOff = leftOff + r.Width;
            minVirtOff = Math.Min(minVirtOff, leftOff);
            maxVirtOff = Math.Max(maxVirtOff, rightOff);
        }

        double minCanvOff  = minVirtOff * _localScale;  // can be negative (peers to the left)
        double maxCanvOff  = maxVirtOff * _localScale;
        double totalSpan   = maxCanvOff - minCanvOff;
        // Centre the span; guarantee padding on the left of the leftmost tile
        double centred     = (CanvasWidth - totalSpan) / 2.0;
        _defaultOriginX    = Math.Max(padding, Math.Max(padding - minCanvOff, centred));

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

        // 5. Prune stale remote keys from _pos
        var validRemote = _rawRects.Keys.ToHashSet();
        foreach (var k in _pos.Keys.Except(_localKeys).Except(validRemote).ToList())
            _pos.Remove(k);

        // 6. Initialise / update remote tile positions.
        //
        // ALL tiles use _localScale — the single consistent scale for the whole canvas.
        // This means:
        //   canvasX = localCanvasLeft + (virtualX - _localMinX) * _localScale
        //   virtualX = _localMinX + (canvasX - localCanvasLeft) / _localScale   (used in Apply)
        //
        // Remote tiles therefore FOLLOW the local cluster when it is dragged, which is correct
        // because the virtual coordinate system is relative to the local cluster's position.
        //
        // Peers whose virtual rect still overlaps the local cluster (both start at 0,0 — not
        // yet explicitly arranged) are placed to the right of the local cluster as a default.
        //
        // The _defaultOriginX is computed above to guarantee all positioned peers have a
        // non-negative canvas X on first render, so no clamping is needed here.

        foreach (var kv in _rawRects)
        {
            var rect = kv.Value;

            // Same scale as local tiles — FIXED regardless of local cluster position
            double rNw = Math.Max(minTileW, rect.Width  * _localScale);
            double rNh = Math.Max(minTileH, rect.Height * _localScale);

            // Canvas position derived from virtual offset relative to local cluster
            double derivedX = localCanvasLeft + (rect.X - _localMinX) * _localScale;
            double derivedY = _defaultOriginY  + (rect.Y - _localMinY) * _localScale;

            // Unpositioned peers (overlapping in virtual space = not yet arranged):
            // place them to the right of the local cluster as a temporary default.
            bool isUnpositioned =
                rect.X < localVirtMaxX && rect.X + rect.Width  > _localMinX &&
                rect.Y < localVirtMaxY && rect.Y + rect.Height > _localMinY;
            if (isUnpositioned)
                derivedX = actualLocalRight;

            if (!_pos.ContainsKey(rect.PeerId))
            {
                _pos[rect.PeerId] = (derivedX, derivedY, derivedX, derivedY, rNw, rNh);
            }
            else
            {
                var e = _pos[rect.PeerId];
                bool pending  = Math.Abs(e.Dx - e.Ax) > 0.5 || Math.Abs(e.Dy - e.Ay) > 0.5;
                bool dragging = rect.PeerId == _dragKey;

                if (dragging || pending)
                    // User is editing — keep draft position, only refresh size
                    _pos[rect.PeerId] = (e.Ax, e.Ay, e.Dx, e.Dy, rNw, rNh);
                else
                    // No edit in progress — re-derive so tile follows local cluster on drag
                    // and stays in sync when a remote position update arrives
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

            // Hard-wall: prevent the block from overlapping any remote tile while dragging
            ResolveBlockOverlaps();

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
        {
            // Hard-wall: prevent this tile from overlapping any other tile while dragging
            (xS, yS) = PushOutOfOverlaps(_dragKey, xS, yS, cur2.Nw, cur2.Nh);
            _pos[_dragKey] = (cur2.Ax, cur2.Ay, xS, yS, cur2.Nw, cur2.Nh);
        }

        RefreshLayout(_layout.Current);
    }

    public void EndDrag()
    {
        if (IsDragging)
        {
            if (_isBlockDrag)
                ResolveBlockOverlaps();
            else if (_dragKey != null && _pos.TryGetValue(_dragKey, out var p))
            {
                var (sx, sy) = PushOutOfOverlaps(_dragKey, p.Dx, p.Dy, p.Nw, p.Nh);
                _pos[_dragKey] = (p.Ax, p.Ay, sx, sy, p.Nw, p.Nh);
            }
            RefreshLayout(_layout.Current);
        }
        IsDragging = false;
        _dragKey   = null;
    }

    // Push a single tile out of all overlapping tiles using minimum-displacement.
    private (double x, double y) PushOutOfOverlaps(string key, double dx, double dy, double nw, double nh)
    {
        const int maxIter = 8;
        for (int i = 0; i < maxIter; i++)
        {
            bool any = false;
            foreach (var kv in _pos)
            {
                if (kv.Key == key) continue;
                var o = kv.Value;
                double ox = Math.Min(dx + nw, o.Dx + o.Nw) - Math.Max(dx, o.Dx);
                double oy = Math.Min(dy + nh, o.Dy + o.Nh) - Math.Max(dy, o.Dy);
                if (ox <= 0 || oy <= 0) continue;
                any = true;
                if (ox <= oy)
                {
                    // Push horizontally
                    bool pushRight = (dx + nw / 2) < (o.Dx + o.Nw / 2);
                    dx = pushRight ? o.Dx - nw : o.Dx + o.Nw;
                }
                else
                {
                    // Push vertically
                    bool pushDown = (dy + nh / 2) < (o.Dy + o.Nh / 2);
                    dy = pushDown ? o.Dy - nh : o.Dy + o.Nh;
                }
                dx = Math.Clamp(dx, 0, CanvasWidth  - nw);
                dy = Math.Clamp(dy, 0, CanvasHeight - nh);
            }
            if (!any) break;
        }
        return (dx, dy);
    }

    // Push the entire local block out of any remote tile it overlaps.
    private void ResolveBlockOverlaps()
    {
        const int maxIter = 8;
        for (int i = 0; i < maxIter; i++)
        {
            bool any = false;
            foreach (var remKey in _rawRects.Keys)
            {
                if (!_pos.TryGetValue(remKey, out var o)) continue;

                // Find any local tile that overlaps this remote tile
                bool overlap = _localKeys.Any(lk =>
                {
                    if (!_pos.TryGetValue(lk, out var lt)) return false;
                    return Math.Min(lt.Dx + lt.Nw, o.Dx + o.Nw) - Math.Max(lt.Dx, o.Dx) > 0 &&
                           Math.Min(lt.Dy + lt.Nh, o.Dy + o.Nh) - Math.Max(lt.Dy, o.Dy) > 0;
                });
                if (!overlap) continue;
                any = true;

                // Compute worst overlap across all local tiles vs this remote tile
                double worstOx = 0, worstOy = 0;
                foreach (var lk in _localKeys)
                {
                    if (!_pos.TryGetValue(lk, out var lt)) continue;
                    double ox = Math.Min(lt.Dx + lt.Nw, o.Dx + o.Nw) - Math.Max(lt.Dx, o.Dx);
                    double oy = Math.Min(lt.Dy + lt.Nh, o.Dy + o.Nh) - Math.Max(lt.Dy, o.Dy);
                    if (ox > 0 && oy > 0 && ox > worstOx) worstOx = ox;
                    if (ox > 0 && oy > 0 && oy > worstOy) worstOy = oy;
                }

                // Compute push for the whole block
                double pushX = 0, pushY = 0;
                if (worstOx <= worstOy)
                {
                    double blockCx = _localKeys.Where(_pos.ContainsKey).Average(k => _pos[k].Dx + _pos[k].Nw / 2);
                    pushX = blockCx < (o.Dx + o.Nw / 2) ? -worstOx : worstOx;
                }
                else
                {
                    double blockCy = _localKeys.Where(_pos.ContainsKey).Average(k => _pos[k].Dy + _pos[k].Nh / 2);
                    pushY = blockCy < (o.Dy + o.Nh / 2) ? -worstOy : worstOy;
                }

                foreach (var lk in _localKeys)
                {
                    if (!_pos.TryGetValue(lk, out var lt)) continue;
                    _pos[lk] = (lt.Ax, lt.Ay,
                        Math.Clamp(lt.Dx + pushX, 0, CanvasWidth  - lt.Nw),
                        Math.Clamp(lt.Dy + pushY, 0, CanvasHeight - lt.Nh),
                        lt.Nw, lt.Nh);
                }
            }
            if (!any) break;
        }
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
