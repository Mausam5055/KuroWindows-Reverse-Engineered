using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using KuroWindows.Input;
using KuroWindows.Input.Touchpad;
using KuroWindows.RecorderCore.Capture;
using KuroWindows.RecorderCore.Clock;
using KuroWindows.RecorderCore.Segments;

namespace KuroWindows.Capture;

internal static class SegmentPreamble
{
	private const int SchemaVersion = 2;

	public static readonly HashSet<string> Types = new HashSet<string> { "header", "clock_anchor", "screen_config", "active_app", "input_settings", "touchpad_device", "input_state", "input_blind" };

	[DllImport("user32.dll")]
	private static extern nint GetThreadDpiAwarenessContext();

	[DllImport("user32.dll")]
	private static extern bool AreDpiAwarenessContextsEqual(nint a, nint b);

	public static void Write(SegmentSet set, DisplayTopologyPolicyResult policy, SegmentOpenReason reason, long clockCorrectionMs, InputPlaneSource? input, RecorderWriteRouter? router)
	{
		SegmentEventFiles files = set.Files;
		if (files.Count == 0)
		{
			return;
		}
		RowSerializer rowSerializer = new RowSerializer();
		long startedWallMs = set.StartedWallMs;
		uint tickCount = (uint)Environment.TickCount;
		ChannelCapabilities channelCapabilities = input?.Channels() ?? default(ChannelCapabilities);
		string dpiContext = DpiContext();
		string appVersion = AppConfig.AppVersion;
		MonitorTable current = MonitorTable.Current;
		using (IEnumerator<SegmentDisplayManifest> enumerator = set.Displays.GetEnumerator())
		{
			_003C_003Ef__AnonymousType11<string, int, string, string, string, int, int, _003C_003Ef__AnonymousType12<int, int, int, int, int, int?, int?, double>, long, _003C_003Ef__AnonymousType13<bool, bool, bool, bool, bool, bool>, string, long> payload;
			int segmentIndex;
			int displayIndex;
			int index;
			int left;
			int top;
			int width;
			int height;
			int? encodedWidth;
			int? encodedHeight;
			double scaleFactor;
			long num;
			SegmentDisplayManifest display;
			for (; enumerator.MoveNext(); payload = new
			{
				type = "header",
				schemaVersion = 2,
				appVersion = appVersion,
				platform = "windows",
				coordinateSpace = "virtual_desktop_px_top_left",
				segmentIndex = segmentIndex,
				displayIndex = displayIndex,
				display = new
				{
					index = index,
					originX = left,
					originY = top,
					width = width,
					height = height,
					clipWidth = encodedWidth,
					clipHeight = encodedHeight,
					scaleFactor = scaleFactor
				},
				seqBase = num,
				channels = new
				{
					hooks = channelCapabilities.Hooks,
					rawMouse = channelCapabilities.RawMouse,
					rawKeyboard = channelCapabilities.RawKeyboard,
					touchpad = channelCapabilities.Touchpad,
					winEvents = channelCapabilities.WinEvents,
					clipboard = channelCapabilities.Clipboard
				},
				dpiContext = dpiContext,
				timestamp = startedWallMs
			}, files.Append(display.DisplayIndex, rowSerializer.SerializeCold(payload, num, tickCount), num))
			{
				display = enumerator.Current;
				CaptureDisplaySettings captureDisplaySettings = policy.Displays.FirstOrDefault((CaptureDisplaySettings d) => d.LogicalDisplayIndex == display.DisplayIndex);
				Rectangle rectangle = captureDisplaySettings?.NativeBounds ?? new Rectangle(0, 0, display.NativeWidth.GetValueOrDefault(), display.NativeHeight.GetValueOrDefault());
				num = EventSeq.Next();
				segmentIndex = set.SegmentIndex;
				displayIndex = display.DisplayIndex;
				index = captureDisplaySettings?.OriginalScreenIndex ?? display.OriginalScreenIndex ?? display.DisplayIndex;
				left = rectangle.Left;
				top = rectangle.Top;
				width = rectangle.Width;
				height = rectangle.Height;
				encodedWidth = display.EncodedWidth;
				encodedHeight = display.EncodedHeight;
				if (captureDisplaySettings != null)
				{
					int num2 = current.IndexOf(captureDisplaySettings.Screen.DeviceName);
					if (num2 >= 0)
					{
						scaleFactor = current.Screens[num2].ScaleFactor;
						continue;
					}
				}
				scaleFactor = 1.0;
			}
		}
		long timestamp = Stopwatch.GetTimestamp();
		var payload2 = new
		{
			type = "clock_anchor",
			timestamp = RecordingClock.Shared.WallMsFromQpc(timestamp),
			qpc = timestamp,
			qpcFrequency = Stopwatch.Frequency,
			tickCount64Ms = Environment.TickCount64,
			utcFiletime100ns = DateTime.UtcNow.ToFileTimeUtc(),
			reason = ((reason == SegmentOpenReason.Rotation) ? "rotation" : "segment_open"),
			correctionMs = clockCorrectionMs
		};
		AppendAll(files, rowSerializer, payload2, tickCount);
		Dictionary<int, int> screenToClip = policy.Displays.ToDictionary((CaptureDisplaySettings d) => d.OriginalScreenIndex, (CaptureDisplaySettings d) => d.LogicalDisplayIndex);
		AppendAll(files, rowSerializer, SystemMonitor.ScreenConfigRow("open", screenToClip, startedWallMs), tickCount);
		KuroWindows.Input.AppContext appContext = input?.Foreground();
		ActiveAppSnapshot activeAppSnapshot = input?.ForegroundWindow();
		if (appContext != null && appContext.BundleId != "")
		{
			var payload3 = new
			{
				type = "active_app",
				name = appContext.AppName,
				bundleId = appContext.BundleId,
				windowTitle = (activeAppSnapshot?.WindowTitle ?? ""),
				browserUrl = activeAppSnapshot?.BrowserUrl,
				pid = appContext.Pid,
				documentPath = (string)null,
				isFullScreen = (activeAppSnapshot?.IsFullScreen == true),
				hwnd = appContext.Hwnd,
				windowClass = appContext.WindowClass,
				aumid = appContext.Aumid,
				timestamp = startedWallMs
			};
			AppendAll(files, rowSerializer, payload3, tickCount);
		}
		InputSettingsSnapshot inputSettingsSnapshot = input?.Settings();
		if (inputSettingsSnapshot != null)
		{
			AppendAll(files, rowSerializer, inputSettingsSnapshot.ToRow("open", startedWallMs), tickCount);
		}
		IReadOnlyList<TouchpadDeviceRecord> readOnlyList = input?.Touchpad();
		if (readOnlyList != null)
		{
			foreach (TouchpadDeviceRecord item in readOnlyList)
			{
				AppendAll(files, rowSerializer, item.ToRow("open", startedWallMs, item.ClaimPreparsed()), tickCount);
			}
		}
		InputStateSnapshot inputStateSnapshot = input?.HeldState();
		if (inputStateSnapshot != null)
		{
			AppendAll(files, rowSerializer, inputStateSnapshot.ToRow("segment_open", startedWallMs), tickCount);
			input.SeedHeldState(inputStateSnapshot);
		}
		if (router != null)
		{
			BlindInterval[] array = router.OpenBlindIntervals(set, startedWallMs);
			foreach (BlindInterval interval in array)
			{
				AppendAll(files, rowSerializer, RecorderWriteRouter.BlindRow(interval, "begin", startedWallMs), tickCount);
			}
		}
		files.Flush();
	}

	private static void AppendAll(SegmentEventFiles files, RowSerializer serializer, object payload, uint osTime)
	{
		long seq = EventSeq.Next();
		files.AppendAll(serializer.SerializeCold(payload, seq, osTime), seq);
	}

	private static string DpiContext()
	{
		try
		{
			nint threadDpiAwarenessContext = GetThreadDpiAwarenessContext();
			if (AreDpiAwarenessContextsEqual(threadDpiAwarenessContext, new IntPtr(-4)))
			{
				return "per_monitor_v2";
			}
			if (AreDpiAwarenessContextsEqual(threadDpiAwarenessContext, new IntPtr(-3)))
			{
				return "per_monitor";
			}
			if (AreDpiAwarenessContextsEqual(threadDpiAwarenessContext, new IntPtr(-2)))
			{
				return "system";
			}
			if (AreDpiAwarenessContextsEqual(threadDpiAwarenessContext, new IntPtr(-1)))
			{
				return "unaware";
			}
			if (AreDpiAwarenessContextsEqual(threadDpiAwarenessContext, new IntPtr(-5)))
			{
				return "unaware_gdiscaled";
			}
		}
		catch
		{
		}
		return "unknown";
	}
}
