using System;
using System.Runtime.InteropServices;
using NexusFlow.Input;
using NexusFlow.Protocol.Input;

namespace NexusFlow.Core.InputInjection;

public sealed class WindowsSendInputInjector : IInputInjector
{
	public void Inject(InputEventV1 ev)
	{
		switch (ev.Kind)
		{
			case InputKind.Key:
				InjectKey(ev.Key!);
				break;

			case InputKind.MouseMove:
				InjectMouseMove(ev.Move!);
				break;

			case InputKind.MouseButton:
				InjectMouseButton(ev.Button!);
				break;

			case InputKind.MouseWheel:
				InjectMouseWheel(ev.Wheel!);
				break;
		}
	}

	// ---------- Keyboard ----------

	private static void InjectKey(InputKeyPayload k)
	{
		var input = new INPUT
		{
			type = INPUT_KEYBOARD,
			U = new InputUnion
			{
				ki = new KEYBDINPUT
				{
					wVk = (ushort)k.VkCode,
					wScan = (ushort)k.ScanCode,
					dwFlags = k.IsDown ? 0u : KEYEVENTF_KEYUP,
					dwExtraInfo = InjectedEventMarker.Magic
				}
			}
		};

		SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
	}

	// ---------- Mouse ----------

	private static void InjectMouseMove(InputMouseMovePayload m)
	{
		var input = new INPUT
		{
			type = INPUT_MOUSE,
			U = new InputUnion
			{
				mi = new MOUSEINPUT
				{
					dx = m.Dx,
					dy = m.Dy,
					dwFlags = MOUSEEVENTF_MOVE,
					dwExtraInfo = InjectedEventMarker.Magic
				}
			}
		};

		SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
	}

	private static void InjectMouseButton(InputMouseButtonPayload b)
	{
		uint flag = b.Button switch
		{
			1 => b.IsDown ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP,
			2 => b.IsDown ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_RIGHTUP,
			3 => b.IsDown ? MOUSEEVENTF_MIDDLEDOWN : MOUSEEVENTF_MIDDLEUP,
			_ => 0
		};

		if (flag == 0) return;

		var input = new INPUT
		{
			type = INPUT_MOUSE,
			U = new InputUnion
			{
				mi = new MOUSEINPUT
				{
					dwFlags = flag,
					dwExtraInfo = InjectedEventMarker.Magic
				}
			}
		};

		SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
	}

	private static void InjectMouseWheel(InputMouseWheelPayload w)
	{
		var input = new INPUT
		{
			type = INPUT_MOUSE,
			U = new InputUnion
			{
				mi = new MOUSEINPUT
				{
					mouseData = (uint)w.Delta,
					dwFlags = MOUSEEVENTF_WHEEL,
					dwExtraInfo = InjectedEventMarker.Magic
				}
			}
		};

		SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
	}

	// ---------- Win32 ----------

	private const uint INPUT_MOUSE = 0;
	private const uint INPUT_KEYBOARD = 1;

	private const uint KEYEVENTF_KEYUP = 0x0002;

	private const uint MOUSEEVENTF_MOVE = 0x0001;
	private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
	private const uint MOUSEEVENTF_LEFTUP = 0x0004;
	private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
	private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
	private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
	private const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
	private const uint MOUSEEVENTF_WHEEL = 0x0800;

	[StructLayout(LayoutKind.Sequential)]
	private struct INPUT
	{
		public uint type;
		public InputUnion U;
	}

	[StructLayout(LayoutKind.Explicit)]
	private struct InputUnion
	{
		[FieldOffset(0)] public MOUSEINPUT mi;
		[FieldOffset(0)] public KEYBDINPUT ki;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct MOUSEINPUT
	{
		public int dx;
		public int dy;
		public uint mouseData;
		public uint dwFlags;
		public uint time;
		public IntPtr dwExtraInfo;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct KEYBDINPUT
	{
		public ushort wVk;
		public ushort wScan;
		public uint dwFlags;
		public uint time;
		public IntPtr dwExtraInfo;
	}

	[DllImport("user32.dll", SetLastError = true)]
	private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

	public void Reset()
	{
		// Release common modifiers to avoid “stuck key” scenarios.
		// This is safe even if they weren't pressed.
		ReleaseVk(0x10); // VK_SHIFT
		ReleaseVk(0x11); // VK_CONTROL
		ReleaseVk(0x12); // VK_MENU (ALT)
		ReleaseVk(0x5B); // VK_LWIN
		ReleaseVk(0x5C); // VK_RWIN
	}

	private static void ReleaseVk(int vk)
	{
		var input = new INPUT
		{
			type = INPUT_KEYBOARD,
			U = new InputUnion
			{
				ki = new KEYBDINPUT
				{
					wVk = (ushort)vk,
					wScan = 0,
					dwFlags = KEYEVENTF_KEYUP,
					dwExtraInfo = InjectedEventMarker.Magic
				}
			}
		};

		SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
	}

}
