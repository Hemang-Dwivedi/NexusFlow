namespace NexusFlow.Display.Models;

public sealed record DisplaySnapshot(
	string StableId,          // best-effort stable identity (EDID/path/etc.)
	int DisplayNumber,        // 1,2,3... (Windows-like)
	bool IsPrimary,
	int X, int Y,             // physical pixels in virtual desktop space
	int Width, int Height,    // physical pixels
	int RotationDegrees,      // 0/90/180/270
	int DpiX, int DpiY        // per-monitor DPI (physical)
);
