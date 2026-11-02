using System;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using KuroWindows.Input.Touchpad;

namespace KuroWindows.Input;

internal sealed class InputSettingsSnapshot
{
	private const uint SPI_GETMOUSE = 3u;

	private const uint SPI_GETKEYBOARDSPEED = 10u;

	private const uint SPI_GETKEYBOARDDELAY = 22u;

	private const uint SPI_GETWHEELSCROLLLINES = 104u;

	private const uint SPI_GETWHEELSCROLLCHARS = 108u;

	private const uint SPI_GETMOUSESPEED = 112u;

	private const int SM_SWAPBUTTON = 23;

	private const int SM_CXDOUBLECLK = 36;

	private const int SM_CYDOUBLECLK = 37;

	public const int DefaultDoubleClickMs = 500;

	public const int DefaultDoubleClickBox = 4;

	private static int _readFailureLogged;

	private string? _serialized;

	public int? Threshold1 { get; private init; }

	public int? Threshold2 { get; private init; }

	public int? Acceleration { get; private init; }

	public int? Speed { get; private init; }

	public bool SwapButtons { get; private init; }

	public int DoubleClickMs { get; private init; } = 500;

	public int DoubleClickWidth { get; private init; } = 4;

	public int DoubleClickHeight { get; private init; } = 4;

	public int? ScrollLines { get; private init; }

	public int? ScrollChars { get; private init; }

	public int? RepeatDelay { get; private init; }

	public int? RepeatSpeed { get; private init; }

	public TouchpadSettingsSnapshot Touchpad { get; }

	public int SystemDpi { get; private init; }

	public string Serialized => _serialized ?? (_serialized = JsonSerializer.Serialize(Body(), RowSerializer.ColdOptions));

	[DllImport("user32.dll", SetLastError = true)]
	private static extern bool SystemParametersInfoW(uint action, uint param, int[] values, uint winIni);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern bool SystemParametersInfoW(uint action, uint param, ref uint value, uint winIni);

	[DllImport("user32.dll")]
	private static extern int GetSystemMetrics(int index);

	[DllImport("user32.dll")]
	private static extern int GetSystemMetricsForDpi(int index, uint dpi);

	[DllImport("user32.dll")]
	private static extern uint GetDoubleClickTime();

	[DllImport("user32.dll")]
	private static extern uint GetDpiForSystem();

	private InputSettingsSnapshot(TouchpadSettingsSnapshot touchpad)
	{
		Touchpad = touchpad;
	}

	public static InputSettingsSnapshot Read()
	{
		TouchpadSettingsSnapshot touchpad;
		try
		{
			touchpad = TouchpadSettings.Read();
		}
		catch (Exception ex)
		{
			LogReadFailure("registry", ex);
			touchpad = new TouchpadSettingsSnapshot("default", null, null, null, null, null, Pan: true, Zoom: true, Reversed: false, null, null, null, Tap: true, TwoFingerTap: true, LeaveOnWithMouse: true);
		}
		int? threshold = null;
		int? threshold2 = null;
		int? acceleration = null;
		int? speed = null;
		int? scrollLines = null;
		int? scrollChars = null;
		int? repeatDelay = null;
		int? repeatSpeed = null;
		bool swapButtons = false;
		int doubleClickMs = 500;
		int doubleClickWidth = 4;
		int doubleClickHeight = 4;
		int systemDpi = 96;
		try
		{
			int[] array = new int[3];
			if (SystemParametersInfoW(3u, 0u, array, 0u))
			{
				threshold = array[0];
				threshold2 = array[1];
				acceleration = array[2];
			}
			uint? num = ReadUInt(112u);
			int? num2;
			if (num.HasValue)
			{
				uint valueOrDefault = num.GetValueOrDefault();
				num2 = (int)valueOrDefault;
			}
			else
			{
				num2 = null;
			}
			speed = num2;
			swapButtons = GetSystemMetrics(23) != 0;
			doubleClickMs = (int)GetDoubleClickTime();
			doubleClickWidth = GetSystemMetricsForDpi(36, 96u);
			doubleClickHeight = GetSystemMetricsForDpi(37, 96u);
			num = ReadUInt(104u);
			int? num3;
			if (num.HasValue)
			{
				uint valueOrDefault2 = num.GetValueOrDefault();
				num3 = ((valueOrDefault2 == uint.MaxValue) ? (-1) : ((int)valueOrDefault2));
			}
			else
			{
				num3 = null;
			}
			scrollLines = num3;
			num = ReadUInt(108u);
			int? num4;
			if (num.HasValue)
			{
				uint valueOrDefault3 = num.GetValueOrDefault();
				num4 = (int)valueOrDefault3;
			}
			else
			{
				num4 = null;
			}
			scrollChars = num4;
			num = ReadUInt(22u);
			int? num5;
			if (num.HasValue)
			{
				uint valueOrDefault4 = num.GetValueOrDefault();
				num5 = (int)valueOrDefault4;
			}
			else
			{
				num5 = null;
			}
			repeatDelay = num5;
			num = ReadUInt(10u);
			int? num6;
			if (num.HasValue)
			{
				uint valueOrDefault5 = num.GetValueOrDefault();
				num6 = (int)valueOrDefault5;
			}
			else
			{
				num6 = null;
			}
			repeatSpeed = num6;
			systemDpi = (int)GetDpiForSystem();
		}
		catch (Exception ex2)
		{
			LogReadFailure("spi", ex2);
		}
		return new InputSettingsSnapshot(touchpad)
		{
			Threshold1 = threshold,
			Threshold2 = threshold2,
			Acceleration = acceleration,
			Speed = speed,
			SwapButtons = swapButtons,
			DoubleClickMs = doubleClickMs,
			DoubleClickWidth = doubleClickWidth,
			DoubleClickHeight = doubleClickHeight,
			ScrollLines = scrollLines,
			ScrollChars = scrollChars,
			RepeatDelay = repeatDelay,
			RepeatSpeed = repeatSpeed,
			SystemDpi = systemDpi
		};
	}

	public object ToRow(string reason, long timestamp)
	{
		return new
		{
			type = "input_settings",
			reason = reason,
			mouse = MouseBlock(),
			wheel = WheelBlock(),
			keyboard = KeyboardBlock(),
			touchpad = Touchpad.ToBlock(),
			systemDpi = SystemDpi,
			timestamp = timestamp
		};
	}

	private object Body()
	{
		return new
		{
			mouse = MouseBlock(),
			wheel = WheelBlock(),
			keyboard = KeyboardBlock(),
			touchpad = Touchpad.ToBlock(),
			systemDpi = SystemDpi
		};
	}

	private object MouseBlock()
	{
		return new
		{
			threshold1 = Threshold1,
			threshold2 = Threshold2,
			acceleration = Acceleration,
			speed = Speed,
			swapButtons = SwapButtons,
			doubleClickMs = DoubleClickMs,
			doubleClickWidth = DoubleClickWidth,
			doubleClickHeight = DoubleClickHeight
		};
	}

	private object WheelBlock()
	{
		return new
		{
			scrollLines = ScrollLines,
			scrollChars = ScrollChars
		};
	}

	private object KeyboardBlock()
	{
		return new
		{
			repeatDelay = RepeatDelay,
			repeatSpeed = RepeatSpeed
		};
	}

	private static uint? ReadUInt(uint action)
	{
		uint value = 0u;
		if (!SystemParametersInfoW(action, 0u, ref value, 0u))
		{
			return null;
		}
		return value;
	}

	private static void LogReadFailure(string source, Exception ex)
	{
		if (Interlocked.Exchange(ref _readFailureLogged, 1) == 0)
		{
			KuroLog.LocalOnly("system", $"input settings read failed source={source}: {ex.GetType().Name}: {ex.Message}", "LogReadFailure", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Input\\InputSettings.cs", 167);
		}
	}
}
