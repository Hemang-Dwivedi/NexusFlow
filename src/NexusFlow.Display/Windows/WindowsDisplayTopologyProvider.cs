using NexusFlow.Display.Models;
using NexusFlow.Identity;
using System.Runtime.InteropServices;

namespace NexusFlow.Display.Windows;

public sealed class WindowsDisplayTopologyProvider : IDisplayTopologyProvider
{
	private readonly ILocalIdentity _me;

	public WindowsDisplayTopologyProvider(ILocalIdentity me)
	{
		_me = me;
	}

	public event EventHandler? TopologyChanged;

	public PeerDisplayCluster GetLocalCluster()
	{
		var screens = System.Windows.Forms.Screen.AllScreens;

		var displays = screens
			.Select((s, idx) =>
			{
				GetDpiForScreen(s, out uint dpiX, out uint dpiY);
				int rotation = GetRotationForScreen(s.DeviceName);

				return new DisplaySnapshot(
					StableId: s.DeviceName,
					DisplayNumber: idx + 1,
					IsPrimary: s.Primary,
					X: s.Bounds.X,
					Y: s.Bounds.Y,
					Width: s.Bounds.Width,
					Height: s.Bounds.Height,
					RotationDegrees: rotation,
					DpiX: (int)dpiX,
					DpiY: (int)dpiY
				);
			})
			.ToList();

		return new PeerDisplayCluster(_me.PeerId, _me.DeviceName, displays);
	}

	// ---- Win32: DPI ----

	private static void GetDpiForScreen(System.Windows.Forms.Screen screen, out uint dpiX, out uint dpiY)
	{
		dpiX = 96;
		dpiY = 96;
		try
		{
			var hmon = MonitorFromPoint(new POINT { X = screen.Bounds.X + 1, Y = screen.Bounds.Y + 1 }, 2 /*MONITOR_DEFAULTTONEAREST*/);
			if (hmon != IntPtr.Zero)
				GetDpiForMonitor(hmon, 0 /*MDT_EFFECTIVE_DPI*/, out dpiX, out dpiY);
		}
		catch { /* fall back to 96 */ }
	}

	[DllImport("Shcore.dll")]
	private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

	[DllImport("user32.dll")]
	private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

	[StructLayout(LayoutKind.Sequential)]
	private struct POINT { public int X; public int Y; }

	// ---- Win32: Rotation ----

	private static int GetRotationForScreen(string deviceName)
	{
		try
		{
			var dm = new DEVMODE();
			dm.dmSize = (ushort)Marshal.SizeOf<DEVMODE>();
			if (EnumDisplaySettings(deviceName, -1 /*ENUM_CURRENT_SETTINGS*/, ref dm))
			{
				return dm.dmDisplayOrientation switch
				{
					0 => 0,
					1 => 90,
					2 => 180,
					3 => 270,
					_ => 0
				};
			}
		}
		catch { /* fall back to 0 */ }
		return 0;
	}

	[DllImport("user32.dll", CharSet = CharSet.Auto)]
	private static extern bool EnumDisplaySettings(string lpszDeviceName, int iModeNum, ref DEVMODE lpDevMode);

	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
	private struct DEVMODE
	{
		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
		public string dmDeviceName;
		public ushort dmSpecVersion;
		public ushort dmDriverVersion;
		public ushort dmSize;
		public ushort dmDriverExtra;
		public uint dmFields;
		public int dmPositionX;
		public int dmPositionY;
		public uint dmDisplayOrientation;
		public uint dmDisplayFixedOutput;
		public short dmColor;
		public short dmDuplex;
		public short dmYResolution;
		public short dmTTOption;
		public short dmCollate;
		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
		public string dmFormName;
		public ushort dmLogPixels;
		public uint dmBitsPerPel;
		public uint dmPelsWidth;
		public uint dmPelsHeight;
		public uint dmDisplayFlags;
		public uint dmDisplayFrequency;
	}
}
