using NexusFlow.Core.Diagnostics;
using NexusFlow.Core.Routing;
using NexusFlow.Display.Layout;
using NexusFlow.Display.Models;
using NexusFlow.Input;
using NexusFlow.Settings.Layout;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace NexusFlow.Core.Input;

/// <summary>
/// Auto-switch target peer when the local cursor exits the local cluster bounds.
/// Works in real pixel space (Windows virtual desktop coordinates).
/// </summary>
public sealed class TargetSwitchingEngine : IDisposable
{
	private const string Cat = "target-switch";

	private readonly IDiagnosticsLog _log;
	private readonly IRoutingEngine _routing;
	private readonly ILayoutStore _layoutStore;

	private readonly PeerDisplayCluster _localCluster;
	private readonly int _edgePx; // how close to edge to trigger
	private long _lastSwitchTicks;

	// External cursor feed (you already have CursorTracker code)
	private readonly ICursorTracker _cursor;

	public TargetSwitchingEngine(
		IDiagnosticsLog log,
		IRoutingEngine routing,
		ILayoutStore layoutStore,
		ICursorTracker cursor,
		PeerDisplayCluster localCluster,
		int edgePx = 2)
	{
		_log = log;
		_routing = routing;
		_layoutStore = layoutStore;
		_cursor = cursor;
		_localCluster = localCluster;
		_edgePx = Math.Max(1, edgePx);

		_cursor.Moved += OnCursorMoved;
	}

	private void OnCursorMoved(int x, int y, int dx, int dy, long ticksUtc)
	{
		// Debounce switches (prevents rapid flip-flop)
		var last = Interlocked.Read(ref _lastSwitchTicks);
		if (ticksUtc - last < TimeSpan.FromMilliseconds(120).Ticks)
			return;

		var bounds = GetLocalClusterBoundsPx(_localCluster);
		if (bounds.Width <= 0 || bounds.Height <= 0) return;

		// Determine if we're trying to leave the local cluster bounds
		var leavingLeft = (dx < 0) && (x <= bounds.Left + _edgePx);
		var leavingRight = (dx > 0) && (x >= bounds.Right - _edgePx);
		var leavingUp = (dy < 0) && (y <= bounds.Top + _edgePx);
		var leavingDown = (dy > 0) && (y >= bounds.Bottom - _edgePx);

		if (!(leavingLeft || leavingRight || leavingUp || leavingDown))
			return;

		var me = _localCluster.PeerId;

		// Load minimal neighbor mapping (you’ll fill this via UI later)
		var neighbors = LoadNeighbors();
		neighbors.Map.TryGetValue(me, out var n);

		var target =
			leavingLeft ? n?.Left :
			leavingRight ? n?.Right :
			leavingUp ? n?.Up :
			leavingDown ? n?.Down :
			null;

		if (string.IsNullOrWhiteSpace(target))
			return;

		// Don’t re-set if already active
		if (string.Equals(_routing.ActiveTargetPeerId, target, StringComparison.Ordinal))
			return;

		Interlocked.Exchange(ref _lastSwitchTicks, ticksUtc);

		_log.Info(Cat, $"Auto-switch target: {me} -> {target}");

		// local-only for now (you can broadcast later)
		_ = _routing.RequestSetActiveTargetAsync(target!);
	}

	private static RectPx GetLocalClusterBoundsPx(PeerDisplayCluster cluster)
	{
		if (cluster.Displays.Count == 0) return default;

		var minX = cluster.Displays.Min(d => d.X);
		var minY = cluster.Displays.Min(d => d.Y);
		var maxX = cluster.Displays.Max(d => d.X + d.Width);
		var maxY = cluster.Displays.Max(d => d.Y + d.Height);

		return new RectPx(minX, minY, maxX, maxY);
	}

	private PeerNeighbors LoadNeighbors()
	{
		// If you want: store inside LayoutState too; for now use a separate store file
		// so you don’t break existing JSON. Easiest:
		//
		// 1) add ILayoutStore method LoadNeighbors/SaveNeighbors, OR
		// 2) temporarily store neighbors inside LayoutState as an extra field.
		//
		// Minimal hack: if not present, return empty.

		try
		{
			// If your JsonLayoutStore currently only supports LayoutState,
			// then for now just return empty and we’ll wire a second JSON file next.
			return new PeerNeighbors();
		}
		catch
		{
			return new PeerNeighbors();
		}
	}

	public void Dispose()
	{
		_cursor.Moved -= OnCursorMoved;
	}

	private readonly record struct RectPx(int Left, int Top, int Right, int Bottom)
	{
		public int Width => Right - Left;
		public int Height => Bottom - Top;
	}
}
