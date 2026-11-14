using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;

namespace HsHelper.Bot
{
	/// <summary>模擬滑鼠操作（加上隨機延遲與抖動），以及偵測爐石是否為前景視窗、熱鍵狀態。</summary>
	public static class Input
	{
		[DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
		[DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
		[DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
		[DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
		[DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
		[DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);

		[StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }

		private const uint LeftDown = 0x0002, LeftUp = 0x0004, RightDown = 0x0008, RightUp = 0x0010;
		public const int VK_F8 = 0x77;

		private static readonly Random Rng = new Random();

		public static int Jitter(int range) { lock(Rng) return Rng.Next(-range, range + 1); }
		public static void Pause(int min, int max) { int ms; lock(Rng) ms = Rng.Next(min, max); Thread.Sleep(ms); }

		public static bool KeyDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

		public static bool HearthstoneInForeground()
		{
			try
			{
				GetWindowThreadProcessId(GetForegroundWindow(), out var pid);
				using var p = Process.GetProcessById((int)pid);
				return p.ProcessName.Equals("Hearthstone", StringComparison.OrdinalIgnoreCase);
			}
			catch(Exception)
			{
				return false;
			}
		}

		private static void MoveTo(Point target, int steps)
		{
			GetCursorPos(out var cur);
			double sx = cur.X, sy = cur.Y;
			for(var i = 1; i <= steps; i++)
			{
				var t = (double)i / steps;
				t = t * t * (3 - 2 * t); // 平滑加減速
				SetCursorPos((int)(sx + (target.X - sx) * t), (int)(sy + (target.Y - sy) * t));
				Pause(10, 22);
			}
		}

		private static Point J(Point p, int r = 4) => new Point(p.X + Jitter(r), p.Y + Jitter(r));

		public static void Click(Point p)
		{
			MoveTo(J(p), 18);
			Pause(180, 320);
			mouse_event(LeftDown, 0, 0, 0, UIntPtr.Zero);
			Pause(70, 140);
			mouse_event(LeftUp, 0, 0, 0, UIntPtr.Zero);
		}

		public static void RightClick()
		{
			mouse_event(RightDown, 0, 0, 0, UIntPtr.Zero);
			Pause(40, 90);
			mouse_event(RightUp, 0, 0, 0, UIntPtr.Zero);
		}

		public static void Drag(Point from, Point to)
		{
			MoveTo(J(from, 3), 18);
			Pause(300, 480); // 讓手牌放大動畫跑完
			mouse_event(LeftDown, 0, 0, 0, UIntPtr.Zero);
			Pause(150, 260);
			MoveTo(J(to), 28);
			Pause(200, 350);
			mouse_event(LeftUp, 0, 0, 0, UIntPtr.Zero);
		}

		public static void Hover(Point p) => MoveTo(J(p, 10), 10);
	}
}
