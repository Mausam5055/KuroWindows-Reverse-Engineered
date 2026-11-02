using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace KuroWindows.Input;

internal sealed class MonitorTable
{
	public sealed record Entry(int Index, Screen Screen, string DeviceName, Rectangle Bounds, Rectangle WorkingArea, bool Primary, double ScaleFactor, uint Dpi, nint Hmon);

	private struct RECT
	{
		public int Left;

		public int Top;

		public int Right;

		public int Bottom;
	}

	private const uint MONITOR_DEFAULTTONEAREST = 2u;

	private const int MDT_EFFECTIVE_DPI = 0;

	private static MonitorTable? _current;

	private readonly Entry[] _screens;

	public static MonitorTable Current
	{
		get
		{
			MonitorTable monitorTable = Volatile.Read(in _current);
			if (monitorTable != null)
			{
				return monitorTable;
			}
			monitorTable = Build();
			return Interlocked.CompareExchange(ref _current, monitorTable, null) ?? monitorTable;
		}
	}

	public IReadOnlyList<Entry> Screens => _screens;

	[DllImport("user32.dll")]
	private static extern nint MonitorFromRect(ref RECT lprc, uint dwFlags);

	[DllImport("user32.dll")]
	private static extern nint MonitorFromPoint(Point pt, uint dwFlags);

	[DllImport("user32.dll")]
	private static extern nint MonitorFromWindow(nint hwnd, uint dwFlags);

	[DllImport("shcore.dll")]
	private static extern int GetDpiForMonitor(nint hmon, int dpiType, out uint dpiX, out uint dpiY);

	private MonitorTable(Entry[] screens)
	{
		_screens = screens;
	}

	public static void Invalidate()
	{
		Volatile.Write(ref _current, null);
	}

	public static MonitorTable Refresh()
	{
		MonitorTable monitorTable = Build();
		Volatile.Write(ref _current, monitorTable);
		return monitorTable;
	}

	public int IndexOf(string deviceName)
	{
		for (int i = 0; i < _screens.Length; i++)
		{
			if (string.Equals(_screens[i].DeviceName, deviceName, StringComparison.OrdinalIgnoreCase))
			{
				return i;
			}
		}
		return -1;
	}

	public int IndexOfNearest(int x, int y)
	{
		int num = IndexOfMonitor(MonitorFromPoint(new Point(x, y), 2u));
		if (num >= 0)
		{
			return num;
		}
		for (int i = 0; i < _screens.Length; i++)
		{
			if (_screens[i].Bounds.Contains(x, y))
			{
				return i;
			}
		}
		return 0;
	}

	public int Locate(int x, int y, out int clampedX, out int clampedY)
	{
		int num = IndexOfNearest(x, y);
		if (_screens.Length == 0)
		{
			clampedX = x;
			clampedY = y;
			return num;
		}
		Rectangle bounds = _screens[num].Bounds;
		clampedX = Math.Clamp(x, bounds.Left, bounds.Right - 1);
		clampedY = Math.Clamp(y, bounds.Top, bounds.Bottom - 1);
		return num;
	}

	public int IndexOfWindow(nint hwnd)
	{
		int num = IndexOfMonitor(MonitorFromWindow(hwnd, 2u));
		if (num < 0)
		{
			return 0;
		}
		return num;
	}

	private int IndexOfMonitor(nint hmon)
	{
		if (hmon == 0)
		{
			return -1;
		}
		for (int i = 0; i < _screens.Length; i++)
		{
			if (_screens[i].Hmon == hmon)
			{
				return i;
			}
		}
		return -1;
	}

	private static MonitorTable Build()
	{
		Screen[] allScreens = Screen.AllScreens;
		Entry[] array = new Entry[allScreens.Length];
		for (int i = 0; i < allScreens.Length; i++)
		{
			Screen screen = allScreens[i];
			Rectangle bounds = screen.Bounds;
			RECT lprc = new RECT
			{
				Left = bounds.Left,
				Top = bounds.Top,
				Right = bounds.Right,
				Bottom = bounds.Bottom
			};
			nint hmon = MonitorFromRect(ref lprc, 2u);
			uint num = ((GetDpiForMonitor(hmon, 0, out var dpiX, out var _) == 0) ? dpiX : 96u);
			array[i] = new Entry(i, screen, screen.DeviceName, bounds, screen.WorkingArea, screen.Primary, (double)num / 96.0, num, hmon);
		}
		return new MonitorTable(array);
	}
}
