using System;

namespace NexusFlow.Input;

/// <summary>
/// Marks events injected by NexusFlow so capture hooks can suppress them.
/// Must be consistent across injector + capture.
/// </summary>
public static class InjectedEventMarker
{
	// 0x4E584653 = 'NXFS' (NexusFlow Suppression)
	public static readonly IntPtr Magic = new IntPtr(unchecked((int)0x4E584653));
}
