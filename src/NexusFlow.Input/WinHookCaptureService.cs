using System.ComponentModel;
using System.Runtime.InteropServices;
namespace NexusFlow.Input;

/// <summary>
/// Low-level Windows input capture (keyboard + mouse).
///
/// The hook owns every input event exclusively. For each non-move event it
/// calls <see cref="ShouldRouteToRemote"/> to decide what to do:
///
///   • Delegate returns true  → event is captured (Key / MouseButton / MouseWheel
///     raised so the orchestrator can forward it to the remote peer) and then
///     BLOCKED locally (hook returns non-zero — Windows never delivers it to
///     any application on this machine).
///
///   • Delegate returns false → event is NOT captured (nothing raised, nothing
///     forwarded) and passed through normally to local applications.
///
/// Mouse moves always raise MouseMove (CursorTracker / TargetSwitchingEngine need
/// deltas regardless of routing state), but the OS cursor is frozen locally when
/// routing to remote — the physical cursor belongs on the remote screen.
///
/// The delegate is read at the moment of every event — no stale flag, no race.
/// </summary>
public interface IWinHookCaptureService
{
	event Action<CapturedKeyEvent>? Key;
	event Action<CapturedMouseMoveEvent>? MouseMove;
	event Action<CapturedMouseButtonEvent>? MouseButton;
	event Action<CapturedMouseWheelEvent>? MouseWheel;

	/// <summary>
	/// Predicate called on the hook thread for every non-move input event.
	/// Return true to capture the event for the remote and block it locally;
	/// return false to pass it through to local applications unchanged.
	/// Set to null when local routing is in effect (equivalent to always false).
	/// </summary>
	Func<bool>? ShouldRouteToRemote { get; set; }

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
	// Tracks whether the previous WM_MOUSEMOVE was injected (InjectedEventMarker.Magic).
	// Used to detect hardware↔injected transitions and reset the delta baseline.
	private bool _lastMoveWasInjected;
	// Tracks whether the previous hardware WM_MOUSEMOVE was in a remote-routing session.
	// On the first event of a new remote session (local→remote transition) P0 must be
	// reset to the current pt rather than the old trigger position.  The trigger position
	// is where the cursor was when boundary detection fired — potentially several pixels
	// outside the screen edge after the snap — which produces a permanent negative offset
	// in every subsequent delta, causing the remote cursor to resist rightward movement.
	private bool _wasRoutingToRemote;

	// Read on the hook thread at the moment of each event — never stale.
	private volatile Func<bool>? _shouldRouteToRemote;

	public Func<bool>? ShouldRouteToRemote
	{
		get => _shouldRouteToRemote;
		set => _shouldRouteToRemote = value;
	}

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
		// Always pass to GlobalHotkeyListener FIRST so the failsafe (Shift+Esc)
		// can trigger regardless of routing state.
		CallNextHookEx(_kbdHook, nCode, wParam, lParam);

		if (nCode < 0)
			return (IntPtr)0;

		try
		{
			var msg = (KeyboardMessage)wParam;
			if (msg is KeyboardMessage.WM_KEYDOWN or KeyboardMessage.WM_SYSKEYDOWN or
				KeyboardMessage.WM_KEYUP or KeyboardMessage.WM_SYSKEYUP)
			{
				var kb = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);

				// Never capture NexusFlow-injected events
				if (kb.dwExtraInfo == InjectedEventMarker.Magic)
					return (IntPtr)0;

				// Ask the routing layer: should this event go to the remote?
				var routeToRemote = _shouldRouteToRemote?.Invoke() ?? false;
				if (!routeToRemote)
					return (IntPtr)0; // pass through to local apps normally

				// Capture for remote forwarding
				var action = (msg is KeyboardMessage.WM_KEYDOWN or KeyboardMessage.WM_SYSKEYDOWN)
					? CapturedKeyAction.Down
					: CapturedKeyAction.Up;

				Key?.Invoke(new CapturedKeyEvent(
					VkCode: kb.vkCode,
					ScanCode: kb.scanCode,
					Flags: kb.flags,
					Action: action,
					TimestampUtcTicks: DateTime.UtcNow.Ticks
				));

				// Block local delivery — event goes to remote, not this machine
				return (IntPtr)1;
			}
		}
		catch
		{
			// swallow — never break global input
		}

		return (IntPtr)0;
	}

	private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
	{
		if (nCode < 0)
			return CallNextHookEx(_mouseHook, nCode, wParam, lParam);

		try
		{
			var msg = (MouseMessage)wParam;
			var ms = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);

			// NexusFlow-injected mouse moves: track position for TargetSwitchingEngine
			// on the RECEIVER so it can detect when the remote cursor reaches a screen
			// edge and auto-switch back. Non-move injected events pass through unchanged.
			//
			// The baseline is reset on the first injected event after hardware events
			// (hardware→injected transition) so the first delta is always clean.
			// Without this reset the first injected dx = injected_pos − last_hardware_pos
			// which is a huge spurious jump that immediately triggers a false boundary cross.
			if (ms.dwExtraInfo == InjectedEventMarker.Magic)
			{
				if (msg == MouseMessage.WM_MOUSEMOVE)
				{
					var ix = ms.pt.x;
					var iy = ms.pt.y;

					if (!_lastMoveWasInjected)
					{
						// First injected event after hardware — just set baseline, no delta.
						_lastX = ix; _lastY = iy; _hasLast = true;
						_lastMoveWasInjected = true;
					}
					else if (_hasLast)
					{
						var idx = ix - _lastX;
						var idy = iy - _lastY;
						if (idx != 0 || idy != 0)
							MouseMove?.Invoke(new CapturedMouseMoveEvent(
								Dx: idx, Dy: idy, X: ix, Y: iy,
								TimestampUtcTicks: DateTime.UtcNow.Ticks));
						_lastX = ix; _lastY = iy;
					}
				}
				return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
			}

			// Hardware event — mark the transition flag so the next injected event
			// gets a fresh baseline rather than a delta from hardware coordinates.
			_lastMoveWasInjected = false;

			// Decide once for this entire event — avoids invoking the delegate twice.
			var routeToRemote = _shouldRouteToRemote?.Invoke() ?? false;

			// ── Mouse moves ───────────────────────────────────────────────────────
			// Always raise MouseMove so CursorTracker / TargetSwitchingEngine receive
			// deltas for boundary detection and remote cursor forwarding.
			//
			// IMPORTANT — reference-point semantics when frozen:
			// Returning (IntPtr)1 keeps the OS cursor at the frozen position P0.
			// Windows then computes every subsequent pt as:
			//   pt[n] = P0 + acceleration(hardware_delta_this_interval)
			// so dx = pt[n] - P0 = acceleration(hardware_delta_n)   [correct]
			//
			// If we updated _lastX/_lastY with pt[n-1] we would get:
			//   dx = pt[n] - pt[n-1] = accel(d_n) - accel(d_{n-1})  [wrong]
			// At constant speed this yields dx=0 → remote cursor stops after frame 1.
			//
			// Fix: when routing to remote, do NOT advance _lastX/_lastY so that P0
			// remains the reference for every event in the frozen session.
			if (msg == MouseMessage.WM_MOUSEMOVE)
			{
				var x = ms.pt.x;
				var y = ms.pt.y;

				if (routeToRemote)
				{
					// ── Remote routing ────────────────────────────────────────────────
					// P0 semantics: _lastX/_lastY stay fixed at the frozen cursor position
					// so that dx = pt[n] - P0 = accel(hardware_delta_n) each frame.
					//
					// CRITICAL — reset P0 on the local→remote transition.
					// The trigger event (where the boundary was detected) runs with
					// routeToRemote=false and sets _lastX to the trigger position, which
					// may be several pixels outside the snapped edge.  The very next event
					// in the remote session (typically the WM_MOUSEMOVE generated by
					// SetCursorPos from SnapCursorToEdge) arrives at the snap position.
					// If we don't reset P0 here, every delta is permanently offset by
					// (snap_x - trigger_x) which is negative, making the remote cursor
					// resist rightward movement even as the user pushes right.
					if (!_wasRoutingToRemote)
					{
						// First event in this remote session — set P0 to current position
						// and skip the delta (it would be trigger→snap noise, not real input).
						_lastX = x; _lastY = y; _hasLast = true;
						_wasRoutingToRemote = true;
						return (IntPtr)1;
					}

					// Ongoing remote session: compute delta against P0 and fire.
					if (_hasLast)
					{
						var dx = x - _lastX;
						var dy = y - _lastY;
						if (dx != 0 || dy != 0)
						{
							MouseMove?.Invoke(new CapturedMouseMoveEvent(
								Dx: dx, Dy: dy, X: x, Y: y,
								TimestampUtcTicks: DateTime.UtcNow.Ticks
							));
						}
					}
					// Do NOT advance _lastX/_lastY — P0 stays fixed for the session.
					return (IntPtr)1; // freeze local cursor
				}

				// ── Local routing ─────────────────────────────────────────────────────
				// Reset the transition flag so the next remote session gets a fresh P0.
				_wasRoutingToRemote = false;

				if (_hasLast)
				{
					var dx = x - _lastX;
					var dy = y - _lastY;
					if (dx != 0 || dy != 0)
					{
						MouseMove?.Invoke(new CapturedMouseMoveEvent(
							Dx: dx, Dy: dy, X: x, Y: y,
							TimestampUtcTicks: DateTime.UtcNow.Ticks
						));
					}
				}

				// Advance the reference and pass through.
				_lastX = x; _lastY = y; _hasLast = true;
				return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
			}

			// ── Buttons and wheel ─────────────────────────────────────────────────
			if (!routeToRemote)
			{
				// Local routing — do not capture, pass event to local applications
				return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
			}

			// Remote routing — capture the event and block local delivery
			switch (msg)
			{
				case MouseMessage.WM_LBUTTONDOWN:
				case MouseMessage.WM_LBUTTONUP:
				case MouseMessage.WM_RBUTTONDOWN:
				case MouseMessage.WM_RBUTTONUP:
				case MouseMessage.WM_MBUTTONDOWN:
				case MouseMessage.WM_MBUTTONUP:
				{
					var (btn, act) = msg switch
					{
						MouseMessage.WM_LBUTTONDOWN => (CapturedMouseButton.Left,   MouseButtonAction.Down),
						MouseMessage.WM_LBUTTONUP   => (CapturedMouseButton.Left,   MouseButtonAction.Up),
						MouseMessage.WM_RBUTTONDOWN => (CapturedMouseButton.Right,  MouseButtonAction.Down),
						MouseMessage.WM_RBUTTONUP   => (CapturedMouseButton.Right,  MouseButtonAction.Up),
						MouseMessage.WM_MBUTTONDOWN => (CapturedMouseButton.Middle, MouseButtonAction.Down),
						MouseMessage.WM_MBUTTONUP   => (CapturedMouseButton.Middle, MouseButtonAction.Up),
						_                           => (CapturedMouseButton.Left,   MouseButtonAction.Down)
					};
					MouseButton?.Invoke(new CapturedMouseButtonEvent(
						Button: btn, Action: act,
						X: ms.pt.x, Y: ms.pt.y,
						TimestampUtcTicks: DateTime.UtcNow.Ticks
					));
					break;
				}

				case MouseMessage.WM_MOUSEWHEEL:
				{
					var delta = (short)((ms.mouseData >> 16) & 0xFFFF);
					MouseWheel?.Invoke(new CapturedMouseWheelEvent(
						Delta: delta,
						X: ms.pt.x, Y: ms.pt.y,
						TimestampUtcTicks: DateTime.UtcNow.Ticks
					));
					break;
				}
			}

			// Block local delivery — event owned by NexusFlow, forwarded to remote
			return (IntPtr)1;
		}
		catch
		{
			// swallow — never break global input
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
