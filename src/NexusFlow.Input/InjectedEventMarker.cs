using System;

namespace NexusFlow.Input;

/// <summary>
/// Marks events injected by NexusFlow so capture hooks can suppress them.
/// Must be consistent across injector + capture.
/// </summary>
public static class InjectedEventMarker
{
	public static readonly IntPtr Magic = new IntPtr(unchecked((int)0xCAFEBABE));
}