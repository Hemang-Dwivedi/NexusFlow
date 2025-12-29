using NexusFlow.Display.Models;
using System.Windows.Forms;
namespace NexusFlow.Display.Windows;

// NOTE: This is a Phase-1 stub that returns correct rectangles.
// We'll upgrade StableId/DPI/rotation with richer Win32 calls next.
public sealed class WindowsDisplayTopologyProvider : IDisplayTopologyProvider
{
	public event EventHandler? TopologyChanged;

	public PeerDisplayCluster GetLocalCluster()
	{
		// TODO: replace with true peer identity later
		var peerId = Environment.MachineName;
		var peerName = Environment.MachineName;

		// Minimal fallback using System.Windows.Forms.Screen for bounds.
		// (Works fine in Windows-only Phase 1. We'll enhance with Win32 for DPI/rotation/hotplug.)
		var screens = System.Windows.Forms.Screen.AllScreens;

		var displays = screens
			.Select((s, idx) => new DisplaySnapshot(
				StableId: s.DeviceName,            // replace later with EDID/path ID
				DisplayNumber: idx + 1,            // Windows-like numbering (approx)
				IsPrimary: s.Primary,
				X: s.Bounds.X,
				Y: s.Bounds.Y,
				Width: s.Bounds.Width,
				Height: s.Bounds.Height,
				RotationDegrees: 0,                // TODO: fill from Win32
				DpiX: 96, DpiY: 96                 // TODO: fill from Win32
			))
			.ToList();

		return new PeerDisplayCluster(peerId, peerName, displays);
	}
}
