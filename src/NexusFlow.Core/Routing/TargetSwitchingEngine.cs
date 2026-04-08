using NexusFlow.Core.Diagnostics;
using NexusFlow.Core.Layout;
using NexusFlow.Core.Services;
using NexusFlow.Identity;
using NexusFlow.Input;

namespace NexusFlow.Core.Routing;

public sealed class TargetSwitchingEngine : IDisposable
{
	private const string Cat = "autoswitch";

	private readonly ILocalIdentity _me;
	private readonly IRoutingEngine _routing;
	private readonly IFailsafeService _failsafe;
	private readonly ILayoutState _layout;
	private readonly ICursorTracker _cursor;
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
		IDiagnosticsLog log)
	{
		_me = me;
		_routing = routing;
		_failsafe = failsafe;
		_layout = layout;
		_cursor = cursor;
		_log = log;

		_snapshot = _layout.Current;
		_layout.Changed += OnLayoutChanged;
		_cursor.Moved += OnCursorMoved;
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
		SnapCursorToEdge(local, exitAxis, dx, dy, (int)px, (int)py);

		// Distributed stamped target switch
		_ = _routing.RequestSetActiveTargetAsync(targetPeerId);

		_log.Info(Cat, $"Auto-switch target -> {targetPeerId} (dx={dx},dy={dy} @ {x},{y})");
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

	private static void SnapCursorToEdge(PeerRect local, Axis axis, int dx, int dy, int currentX, int currentY)
	{
		try
		{
			int ex = currentX, ey = currentY;
			if (axis == Axis.Horizontal)
				ex = dx > 0 ? (int)(local.X + local.Width - 1) : (int)local.X;
			else
				ey = dy > 0 ? (int)(local.Y + local.Height - 1) : (int)local.Y;
			SetCursorPos(ex, ey);
		}
		catch { }
	}

	[System.Runtime.InteropServices.DllImport("user32.dll")]
	private static extern bool SetCursorPos(int X, int Y);

	public void Dispose()
	{
		_cursor.Moved -= OnCursorMoved;
		_layout.Changed -= OnLayoutChanged;
	}
}
