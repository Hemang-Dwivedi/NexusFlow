using System.Runtime.InteropServices;

namespace NexusFlow.Input;

public sealed class GlobalHotkeyListener : IDisposable
{
	private IntPtr _hook = IntPtr.Zero;
	private LowLevelKeyboardProc? _proc;

	private volatile bool _shiftDown;
	private volatile bool _blockedFired; // prevent repeat spam while held

	public event Action? ShiftEscPressed;

	public void Start()
	{
		if (_hook != IntPtr.Zero) return;

		_proc = HookCallback;
		_hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
		if (_hook == IntPtr.Zero)
			throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Failed to install keyboard hook.");
	}

	public void Stop()
	{
		if (_hook == IntPtr.Zero) return;

		UnhookWindowsHookEx(_hook);
		_hook = IntPtr.Zero;
		_proc = null;

		_shiftDown = false;
		_blockedFired = false;
	}

	public void Dispose() => Stop();

	private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
	{
		if (nCode >= 0)
		{
			var msg = (KeyboardMessage)wParam;
			var kb = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);

			// Track shift state (both L/R shift)
			if (kb.vkCode is VK_LSHIFT or VK_RSHIFT)
			{
				if (msg is KeyboardMessage.WM_KEYDOWN or KeyboardMessage.WM_SYSKEYDOWN)
					_shiftDown = true;
				else if (msg is KeyboardMessage.WM_KEYUP or KeyboardMessage.WM_SYSKEYUP)
				{
					_shiftDown = false;
					_blockedFired = false;
				}
			}

			// Detect Shift + Esc
			if (kb.vkCode == VK_ESCAPE)
			{
				if (msg is KeyboardMessage.WM_KEYDOWN or KeyboardMessage.WM_SYSKEYDOWN)
				{
					if (_shiftDown && !_blockedFired)
					{
						_blockedFired = true;
						ShiftEscPressed?.Invoke();
					}
				}
				else if (msg is KeyboardMessage.WM_KEYUP or KeyboardMessage.WM_SYSKEYUP)
				{
					_blockedFired = false;
				}
			}
		}

		return CallNextHookEx(_hook, nCode, wParam, lParam);
	}

	// ---- Win32 interop ----

	private const int WH_KEYBOARD_LL = 13;

	private const int VK_ESCAPE = 0x1B;
	private const int VK_LSHIFT = 0xA0;
	private const int VK_RSHIFT = 0xA1;

	private enum KeyboardMessage : int
	{
		WM_KEYDOWN = 0x0100,
		WM_KEYUP = 0x0101,
		WM_SYSKEYDOWN = 0x0104,
		WM_SYSKEYUP = 0x0105
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct KBDLLHOOKSTRUCT
	{
		public int vkCode;
		public int scanCode;
		public int flags;
		public int time;
		public IntPtr dwExtraInfo;
	}

	private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

	[DllImport("user32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool UnhookWindowsHookEx(IntPtr hhk);

	[DllImport("user32.dll")]
	private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

	[DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
	private static extern IntPtr GetModuleHandle(string? lpModuleName);
}
