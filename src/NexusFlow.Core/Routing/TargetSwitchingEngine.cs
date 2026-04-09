using NexusFlow.Core.Diagnostics;
using NexusFlow.Core.Layout;
using NexusFlow.Core.Services;
using NexusFlow.Identity;
using NexusFlow.Input;
using NexusFlow.Protocol.Control;

namespace NexusFlow.Core.Routing;

public sealed class TargetSwitchingEngine : IDisposable
{
	private const string Cat = "autoswitch";

	private readonly ILocalIdentity _me;
	private readonly IRoutingEngine _routing;
	private readonly IFailsafeService _failsafe;
	private readonly ILayoutState _layout;
	private readonly ICursorTracker _cursor;
	private readonly IWinHookCaptureService _capture;
	private readonly IDiagnosticsLog _log;

	private LayoutSnapshot? _snapshot;

	// hysteresis
	private const double ExitMarginPx = 8; // small margin to avoid flapping on boundary
	private static readonly long MinSwitchTicks = TimeSpan.FromMilliseconds(150).Ticks;
	private long _lastSwitchTicks;

	public bool Enabled { get; set; } = true;

	public TargetSwitchingEngine(
		ILocalIdentity me,
		IRoutingEngine routing,
		IFailsafeService failsafe,
		ILayoutState layout,
		ICursorTracker cursor,
		IWinHookCaptureService capture,
		IDiagnosticsLog log)
	{
		_me = me;
		_routing = routing;
		_failsafe = failsafe;
		_layout = layout;
		_cursor = cursor;
		_capture = capture;
		_log = log;

		_snapshot = _layout.Current;
		_layout.Changed += OnLayoutChanged;
		_cursor.Moved += OnCursorMoved;
		_routing.CursorWarpRequested += OnCursorWarpRequested;
	}

	private void OnLayoutChanged(LayoutSnapshot? snap) => _snapshot = snap;

	private void OnCursorMoved(int x, int y, int dx, int dy, long ticks)
	{
		if (!Enabled) return;
		if (_failsafe.IsBlocked) return;

		var snap = _snapshot;
		if (snap is null) return;

		if (!snap.TryGetPeerRect(_me.PeerId, out var local))
			return;

		var px = (double)x;
		var py = (double)y;

		// Still inside local (with a small margin) -> no switch attempts
		if (IsInsideWithMargin(local, px, py, ExitMarginPx))
			return;

		// Decide which edge we *intended* to cross using RELATIVE motion
		var exitAxis = DominantAxis(dx, dy);

		// Require "outside" in the intended direction (prevents diagonal flaps)
		if (!IsOutsideInAxis(local, px, py, ExitMarginPx, exitAxis))
			return;

		// Probe just outside the local boundary in the direction of movement
		// so remote peers positioned at the edge are found correctly
		var probeX = px;
		var probeY = py;
		if (exitAxis == Axis.Horizontal)
		{
			if (dx > 0) probeX = local.X + local.Width + ExitMarginPx;  // right edge probe
			else probeX = local.X - ExitMarginPx - 1;                    // left edge probe
		}
		else
		{
			if (dy > 0) probeY = local.Y + local.Height + ExitMarginPx;  // bottom edge probe
			else probeY = local.Y - ExitMarginPx - 1;                    // top edge probe
		}

		if (!snap.TryFindPeerAt(probeX, probeY, out var targetPeerId))
			return;

		if (string.Equals(targetPeerId, _me.PeerId, StringComparison.Ordinal))
			return;

		// cooldown to avoid rapid oscillation
		var last = Volatile.Read(ref _lastSwitchTicks);
		if (ticks - last < MinSwitchTicks)
			return;

		Volatile.Write(ref _lastSwitchTicks, ticks);

		// Snap cursor to the exact edge pixel before the hook freezes it.
		// Without this the cursor stops wherever the OS last placed it,
		// which may be a few pixels past the boundary — off the physical screen.
		var (snappedX, snappedY) = SnapCursorToEdge(local, exitAxis, dx, dy, (int)px, (int)py);

		// Force P0 to the snap position NOW, while still on the hook thread and inside the
		// current callback chain.  This prevents the hook callback's local-path from
		// overwriting _lastX/_lastY with the trigger position (which is outside the screen
		// edge and causes a permanent negative delta offset → remote cursor resists rightward
		// movement).  The _p0IsLocked flag in WinHookCaptureService guards the overwrite.
		_capture.SetP0(snappedX, snappedY);

		// Only include entry warp info on a genuine first switch (when we are currently routing
		// to ourselves). If we are already routing to this peer (re-assertion after 150ms cooldown
		// because the frozen cursor is still at the boundary), do NOT include entry info — the
		// receiver would warp back to the entry edge on every re-assertion, making the cursor stick.
		var isNewSwitch = string.Equals(_routing.ActiveTargetPeerId, _me.PeerId, StringComparison.Ordinal);
		var (entryEdge, entryNormalized) = isNewSwitch
			? ComputeEntryInfo(local, exitAxis, dx, dy, snappedX, snappedY)
			: (EntryEdge.None, 0.5);

		// Distributed stamped target switch — includes cursor warp hint on first switch only.
		_ = _routing.RequestSetActiveTargetAsync(targetPeerId, entryEdge, entryNormalized);

		_log.Info(Cat, $"Auto-switch target -> {targetPeerId} (dx={dx},dy={dy} @ {x},{y}) entry={entryEdge}@{entryNormalized:F2}");
	}

	private static Axis DominantAxis(int dx, int dy)
		=> Math.Abs(dx) >= Math.Abs(dy) ? Axis.Horizontal : Axis.Vertical;

	private static bool IsInsideWithMargin(PeerRect r, double x, double y, double margin)
		=> x >= r.X + margin &&
		   y >= r.Y + margin &&
		   x < (r.X + r.Width - margin) &&
		   y < (r.Y + r.Height - margin);

	private static bool IsOutsideInAxis(PeerRect r, double x, double y, double margin, Axis axis)
	{
		return axis switch
		{
			Axis.Horizontal => x < r.X + margin || x >= (r.X + r.Width - margin),
			Axis.Vertical   => y < r.Y + margin || y >= (r.Y + r.Height - margin),
			_ => true
		};
	}

	private enum Axis { Horizontal, Vertical }

	private static (int X, int Y) SnapCursorToEdge(PeerRect local, Axis axis, int dx, int dy, int currentX, int currentY)
	{
		int ex = currentX, ey = currentY;
		try
		{
			if (axis == Axis.Horizontal)
				ex = dx > 0 ? (int)(local.X + local.Width - 1) : (int)local.X;
			else
				ey = dy > 0 ? (int)(local.Y + local.Height - 1) : (int)local.Y;
			SetCursorPos(ex, ey);
		}
		catch { }
		return (ex, ey);
	}

	/// <summary>
	/// Computes the entry edge and normalized position (0–1) along that edge for the remote peer.
	/// "Entry edge" is the opposite of our exit edge: if we leave via Right, B enters from Left.
	/// "Normalized" is the fractional position along the perpendicular axis so B can proportionally
	/// place the cursor even when A and B have different resolutions.
	/// </summary>
	private static (EntryEdge Edge, double Normalized) ComputeEntryInfo(
		PeerRect local, Axis exitAxis, int dx, int dy, int snappedX, int snappedY)
	{
		if (exitAxis == Axis.Horizontal)
		{
			var normalized = local.Height > 0 ? (snappedY - local.Y) / local.Height : 0.5;
			normalized = Math.Clamp(normalized, 0.0, 1.0);
			return (dx > 0 ? EntryEdge.Left : EntryEdge.Right, normalized);
		}
		else
		{
			var normalized = local.Width > 0 ? (snappedX - local.X) / local.Width : 0.5;
			normalized = Math.Clamp(normalized, 0.0, 1.0);
			return (dy > 0 ? EntryEdge.Top : EntryEdge.Bottom, normalized);
		}
	}

	/// <summary>
	/// Called on the receiving peer (B) when A switches routing to us.
	/// Warps cursor to the entry edge position so movement direction feels natural.
	/// </summary>
	private void OnCursorWarpRequested(EntryEdge edge, double normalized)
	{
		var snap = _snapshot;
		if (snap is null) return;
		if (!snap.TryGetPeerRect(_me.PeerId, out var local)) return;

		// Place cursor just inside the entry edge (WarpInsetPx) to avoid immediately
		// triggering switch-back, which fires when cursor is within ExitMarginPx of an edge.
		const int WarpInsetPx = 20;

		int wx, wy;
		switch (edge)
		{
			case EntryEdge.Left:
				wx = (int)(local.X + WarpInsetPx);
				wy = (int)(local.Y + normalized * local.Height);
				break;
			case EntryEdge.Right:
				wx = (int)(local.X + local.Width - 1 - WarpInsetPx);
				wy = (int)(local.Y + normalized * local.Height);
				break;
			case EntryEdge.Top:
				wx = (int)(local.X + normalized * local.Width);
				wy = (int)(local.Y + WarpInsetPx);
				break;
			case EntryEdge.Bottom:
				wx = (int)(local.X + normalized * local.Width);
				wy = (int)(local.Y + local.Height - 1 - WarpInsetPx);
				break;
			default:
				return;
		}

		try { SetCursorPos(wx, wy); }
		catch { }

		_log.Info(Cat, $"Cursor warped to entry {edge}@{normalized:F2} -> ({wx},{wy})");
	}

	[System.Runtime.InteropServices.DllImport("user32.dll")]
	private static extern bool SetCursorPos(int X, int Y);

	public void Dispose()
	{
		_cursor.Moved -= OnCursorMoved;
		_layout.Changed -= OnLayoutChanged;
		_routing.CursorWarpRequested -= OnCursorWarpRequested;
	}
}
