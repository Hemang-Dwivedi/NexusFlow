using System.ComponentModel;
using System.Runtime.InteropServices;
namespace NexusFlow.Input;

/// <summary>
/// Low-level Windows input capture (keyboard + mouse).
/// Emits events but never blocks input (always calls CallNextHookEx).
/// No injection. No routing. Pure capture.
/// </summary>

public interface IWinHookCaptureService
{
	event Action<CapturedKeyEvent>? Key;
	event Action<CapturedMouseMoveEvent>? MouseMove;
	event Action<CapturedMouseButtonEvent>? MouseButton;
	event Action<CapturedMouseWheelEvent>? MouseWheel;

	void Start();
	void Stop();
}

public sealed class WinHookCaptureService : IWinHookCaptureService, IDisposable
{
	private const int WH_KEYBOARD_LL = 13;
	private const int WH_MOUSE_LL = 14;

	private Thread? _thread;
	private uint _threadId;

	private readonly ManualResetEventSlim _started = new(false);
	private readonly ManualResetEventSlim _stopped = new(false);
	private volatile bool _run;

	private IntPtr _kbdHook = IntPtr.Zero;
	private IntPtr _mouseHook = IntPtr.Zero;

	private LowLevelKeyboardProc? _kbdProc;
	private LowLevelMouseProc? _mouseProc;

	private int _lastX;
	private int _lastY;
	private bool _hasLast;

	public event Action<CapturedKeyEvent>? Key;
	public event Action<CapturedMouseMoveEvent>? MouseMove;
	public event Action<CapturedMouseButtonEvent>? MouseButton;
	public event Action<CapturedMouseWheelEvent>? MouseWheel;

	public void Start()
	{
		// idempotent
		if (_thread is not null) return;

		_run = true;
		_started.Reset();
		_stopped.Reset();

		_thread = new Thread(HookThreadMain)
		{
			IsBackground = true,
			Name = "NexusFlow.Input.WinHookCapture"
		};
		_thread.Start();

		_started.Wait();

		// If hooks failed, thread would have signaled started but not installed hooks.
		if (_kbdHook == IntPtr.Zero || _mouseHook == IntPtr.Zero)
		{
			Stop();
			throw new InvalidOperationException("WinHookCaptureService failed to install one or more hooks.");
		}
	}

	public void Stop()
	{
		var t = _thread;
		if (t is null) return;

		_run = false;

		// Ask the hook thread to quit its message loop
		if (_threadId != 0)
			PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);

		_stopped.Wait(TimeSpan.FromSeconds(2));

		_thread = null;
		_threadId = 0;
	}

	public void Dispose()
	{
		Stop();
		_started.Dispose();
		_stopped.Dispose();
	}

	private void HookThreadMain()
	{
		try
		{
			_threadId = GetCurrentThreadId();

			// Keep delegates alive
			_kbdProc = KbdHookCallback;
			_mouseProc = MouseHookCallback;

			var hMod = GetModuleHandle(null);

			_kbdHook = SetWindowsHookEx(WH_KEYBOARD_LL, _kbdProc, hMod, 0);
			if (_kbdHook == IntPtr.Zero)
				throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to install WH_KEYBOARD_LL.");

			_mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, hMod, 0);
			if (_mouseHook == IntPtr.Zero)
				throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to install WH_MOUSE_LL.");

			_hasLast = false;

			_started.Set();

			// Message loop required for reliable LL hook delivery
			while (_run && GetMessage(out var msg, IntPtr.Zero, 0, 0) != 0)
			{
				TranslateMessage(ref msg);
				DispatchMessage(ref msg);
			}
		}
		catch
		{
			// Ensure Start() unblocks and caller sees failure
			_started.Set();
		}
		finally
		{
			try
			{
				if (_kbdHook != IntPtr.Zero)
				{
					UnhookWindowsHookEx(_kbdHook);
					_kbdHook = IntPtr.Zero;
				}
				if (_mouseHook != IntPtr.Zero)
				{
					UnhookWindowsHookEx(_mouseHook);
					_mouseHook = IntPtr.Zero;
				}
			}
			catch { /* never throw on cleanup */ }

			_kbdProc = null;
			_mouseProc = null;

			_stopped.Set();
		}
	}

	private IntPtr KbdHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
	{
		try
		{
			if (nCode >= 0)
			{
				var msg = (KeyboardMessage)wParam;
				if (msg is KeyboardMessage.WM_KEYDOWN or KeyboardMessage.WM_SYSKEYDOWN or
					KeyboardMessage.WM_KEYUP or KeyboardMessage.WM_SYSKEYUP)
				{
					var kb = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);

					var action = (msg is KeyboardMessage.WM_KEYDOWN or KeyboardMessage.WM_SYSKEYDOWN)
						? CapturedKeyAction.Down
						: CapturedKeyAction.Up;

					Key?.Invoke(new CapturedKeyEvent(
						VkCode: kb.vkCode,
						ScanCode: kb.scanCode,
						Action: action,
						TimestampUtcTicks: DateTime.UtcNow.Ticks
					));
				}
			}
		}
		catch
		{
			// swallow - never break global input
		}

		return CallNextHookEx(_kbdHook, nCode, wParam, lParam);
	}

	private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
	{
		try
		{
			if (nCode >= 0)
			{
				var msg = (MouseMessage)wParam;
				var ms = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);

				switch (msg)
				{
					case MouseMessage.WM_MOUSEMOVE:
						{
							var x = ms.pt.x;
							var y = ms.pt.y;

							if (!_hasLast)
							{
								_lastX = x; _lastY = y; _hasLast = true;
								break;
							}

							var dx = x - _lastX;
							var dy = y - _lastY;
							_lastX = x; _lastY = y;

							if (dx != 0 || dy != 0)
							{
								MouseMove?.Invoke(new CapturedMouseMoveEvent(
									Dx: dx, Dy: dy,
									X: x, Y: y,
									TimestampUtcTicks: DateTime.UtcNow.Ticks
								));
							}
							break;
						}

					case MouseMessage.WM_LBUTTONDOWN:
					case MouseMessage.WM_LBUTTONUP:
					case MouseMessage.WM_RBUTTONDOWN:
					case MouseMessage.WM_RBUTTONUP:
					case MouseMessage.WM_MBUTTONDOWN:
					case MouseMessage.WM_MBUTTONUP:
						{
							var (btn, act) = msg switch
							{
								MouseMessage.WM_LBUTTONDOWN => (CapturedMouseButton.Left, MouseButtonAction.Down),
								MouseMessage.WM_LBUTTONUP => (CapturedMouseButton.Left, MouseButtonAction.Up),
								MouseMessage.WM_RBUTTONDOWN => (CapturedMouseButton.Right, MouseButtonAction.Down),
								MouseMessage.WM_RBUTTONUP => (CapturedMouseButton.Right, MouseButtonAction.Up),
								MouseMessage.WM_MBUTTONDOWN => (CapturedMouseButton.Middle, MouseButtonAction.Down),
								MouseMessage.WM_MBUTTONUP => (CapturedMouseButton.Middle, MouseButtonAction.Up),
								_ => (CapturedMouseButton.Left, MouseButtonAction.Down)
							};

							MouseButton?.Invoke(new CapturedMouseButtonEvent(
								Button: btn,
								Action: act,
								X: ms.pt.x,
								Y: ms.pt.y,
								TimestampUtcTicks: DateTime.UtcNow.Ticks
							));
							break;
						}

					case MouseMessage.WM_MOUSEWHEEL:
						{
							var delta = (short)((ms.mouseData >> 16) & 0xFFFF);
							MouseWheel?.Invoke(new CapturedMouseWheelEvent(
								Delta: delta,
								X: ms.pt.x,
								Y: ms.pt.y,
								TimestampUtcTicks: DateTime.UtcNow.Ticks
							));
							break;
						}
				}
			}
		}
		catch
		{
			// swallow - never break global input
		}

		return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
	}

	// -------- Win32 interop --------

	private const uint WM_QUIT = 0x0012;

	private enum KeyboardMessage : int
	{
		WM_KEYDOWN = 0x0100,
		WM_KEYUP = 0x0101,
		WM_SYSKEYDOWN = 0x0104,
		WM_SYSKEYUP = 0x0105
	}

	private enum MouseMessage : int
	{
		WM_MOUSEMOVE = 0x0200,
		WM_LBUTTONDOWN = 0x0201,
		WM_LBUTTONUP = 0x0202,
		WM_RBUTTONDOWN = 0x0204,
		WM_RBUTTONUP = 0x0205,
		WM_MBUTTONDOWN = 0x0207,
		WM_MBUTTONUP = 0x0208,
		WM_MOUSEWHEEL = 0x020A
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct POINT { public int x; public int y; }

	[StructLayout(LayoutKind.Sequential)]
	private struct MSLLHOOKSTRUCT
	{
		public POINT pt;
		public int mouseData;
		public int flags;
		public int time;
		public IntPtr dwExtraInfo;
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

	[StructLayout(LayoutKind.Sequential)]
	private struct MSG
	{
		public IntPtr hwnd;
		public uint message;
		public IntPtr wParam;
		public IntPtr lParam;
		public uint time;
		public POINT pt;
		public uint lPrivate;
	}

	private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);
	private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

	[DllImport("user32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool UnhookWindowsHookEx(IntPtr hhk);

	[DllImport("user32.dll")]
	private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern sbyte GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

	[DllImport("user32.dll")]
	private static extern bool TranslateMessage(ref MSG lpMsg);

	[DllImport("user32.dll")]
	private static extern IntPtr DispatchMessage(ref MSG lpMsg);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

	[DllImport("kernel32.dll")]
	private static extern uint GetCurrentThreadId();

	[DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
	private static extern IntPtr GetModuleHandle(string? lpModuleName);
}
