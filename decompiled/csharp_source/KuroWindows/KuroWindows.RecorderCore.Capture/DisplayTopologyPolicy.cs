using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using KuroWindows.Capture;
using KuroWindows.Input;

namespace KuroWindows.RecorderCore.Capture;

internal static class DisplayTopologyPolicy
{
	private readonly record struct ScreenEntry(int OriginalIndex, Screen Screen);

	[Flags]
	private enum QueryDisplayConfigFlags : uint
	{
		OnlyActivePaths = 2u,
		VirtualModeAware = 0x10u
	}

	private struct Luid
	{
		public uint LowPart;

		public int HighPart;
	}

	private struct DisplayConfigPathSourceInfo
	{
		public Luid adapterId;

		public uint id;

		public uint modeInfoIdx;

		public uint statusFlags;
	}

	private struct DisplayConfigRational
	{
		public uint Numerator;

		public uint Denominator;
	}

	private struct DisplayConfigPathTargetInfo
	{
		public Luid adapterId;

		public uint id;

		public uint modeInfoIdx;

		public int outputTechnology;

		public int rotation;

		public int scaling;

		public DisplayConfigRational refreshRate;

		public int scanLineOrdering;

		[MarshalAs(UnmanagedType.Bool)]
		public bool targetAvailable;

		public uint statusFlags;
	}

	private struct DisplayConfigPathInfo
	{
		public DisplayConfigPathSourceInfo sourceInfo;

		public DisplayConfigPathTargetInfo targetInfo;

		public uint flags;
	}

	private struct DisplayConfigModeInfo
	{
		public uint infoType;

		public uint id;

		public Luid adapterId;

		public DisplayConfigModeInfoUnion modeInfo;
	}

	[StructLayout(LayoutKind.Explicit)]
	private struct DisplayConfigModeInfoUnion
	{
		[FieldOffset(0)]
		public DisplayConfigTargetMode targetMode;

		[FieldOffset(0)]
		public DisplayConfigSourceMode sourceMode;

		[FieldOffset(0)]
		public DisplayConfigDesktopImageInfo desktopImageInfo;
	}

	private struct DisplayConfigTargetMode
	{
		public DisplayConfigVideoSignalInfo targetVideoSignalInfo;
	}

	private struct DisplayConfigVideoSignalInfo
	{
		public ulong pixelRate;

		public DisplayConfigRational hSyncFreq;

		public DisplayConfigRational vSyncFreq;

		public DisplayConfigTwoDRegion activeSize;

		public DisplayConfigTwoDRegion totalSize;

		public uint videoStandard;

		public int scanLineOrdering;
	}

	private struct DisplayConfigTwoDRegion
	{
		public uint cx;

		public uint cy;
	}

	private struct DisplayConfigSourceMode
	{
		public uint width;

		public uint height;

		public int pixelFormat;

		public PointL position;
	}

	private struct PointL
	{
		public int x;

		public int y;
	}

	private struct DisplayConfigDesktopImageInfo
	{
		public PointL PathSourceSize;

		public RectangleL DesktopImageRegion;

		public RectangleL DesktopImageClip;
	}

	private struct RectangleL
	{
		public int Left;

		public int Top;

		public int Right;

		public int Bottom;
	}

	public const string UnsupportedTooManyDisplays = "unsupported_topology_too_many_displays";

	public const int SecondaryResolutionFloor = 1080;

	private static readonly object TopologyEpochSync = new object();

	private static int _topologyEpoch;

	private static string? _lastTopologySignature;

	private static bool _nativeBoundsFallbackLogged;

	private static bool _displayConfigProbeFailureLogged;

	public static DisplayTopologyPolicyResult Evaluate(AdaptiveCaptureProfile profile)
	{
		ScreenEntry[] array = (from entry in MonitorTable.Refresh().Screens
			select new ScreenEntry(entry.Index, entry.Screen) into s
			orderby s.Screen.Primary descending, s.Screen.Bounds.Left, s.Screen.Bounds.Top
			select s).ToArray();
		int num = Math.Max(array.Length, TryGetActivePathCount() ?? array.Length);
		bool duplicated = IsDuplicatedTopology(array);
		DisplayTopologyMode displayTopologyMode = ModeFor(array.Length, num, duplicated);
		string text = BuildTopologySignature(array, num, duplicated);
		int topologyEpoch = EpochForSignature(text);
		switch (displayTopologyMode)
		{
		case DisplayTopologyMode.ExtendedTooMany:
			return new DisplayTopologyPolicyResult(displayTopologyMode, topologyEpoch, num, 0, text, "unsupported_topology_too_many_displays", profile, Array.Empty<CaptureDisplaySettings>());
		default:
			if (array.Length != 0)
			{
				CaptureDisplaySettings[] array2 = SelectScreens(array, duplicated, profile).Select((ScreenEntry entry, int logicalIndex) => BuildDisplaySettings(entry, logicalIndex, profile)).ToArray();
				return new DisplayTopologyPolicyResult(displayTopologyMode, topologyEpoch, num, array2.Length, text, null, profile, array2);
			}
			goto case DisplayTopologyMode.Unsupported;
		case DisplayTopologyMode.Unsupported:
			return new DisplayTopologyPolicyResult(DisplayTopologyMode.Unsupported, topologyEpoch, num, 0, text, "unsupported_display_topology", profile, Array.Empty<CaptureDisplaySettings>());
		}
	}

	public static AdaptiveCaptureProfile ProfileForIndex(int index)
	{
		if (index > 0)
		{
			return index switch
			{
				1 => new AdaptiveCaptureProfile(1, "profile_1", 30, 15, SecondaryDownscaleTo1080Floor: false, PrimaryOnly: false), 
				2 => new AdaptiveCaptureProfile(2, "profile_2", 15, 15, SecondaryDownscaleTo1080Floor: false, PrimaryOnly: false), 
				3 => new AdaptiveCaptureProfile(3, "profile_3", 15, 15, SecondaryDownscaleTo1080Floor: true, PrimaryOnly: false), 
				4 => new AdaptiveCaptureProfile(4, "profile_4", 15, 15, SecondaryDownscaleTo1080Floor: false, PrimaryOnly: true), 
				_ => new AdaptiveCaptureProfile(5, "profile_5", 10, 10, SecondaryDownscaleTo1080Floor: false, PrimaryOnly: true), 
			};
		}
		return new AdaptiveCaptureProfile(0, "profile_0", 30, 30, SecondaryDownscaleTo1080Floor: false, PrimaryOnly: false);
	}

	internal static DisplayTopologyMode ModeFor(int logicalCount, int physicalCount, bool duplicated)
	{
		if (duplicated && logicalCount >= 1 && logicalCount <= 2)
		{
			return DisplayTopologyMode.Duplicated;
		}
		if (logicalCount > 0)
		{
			return logicalCount switch
			{
				1 => (physicalCount > 1) ? DisplayTopologyMode.Duplicated : DisplayTopologyMode.Single, 
				2 => DisplayTopologyMode.ExtendedTwo, 
				_ => DisplayTopologyMode.ExtendedTooMany, 
			};
		}
		return DisplayTopologyMode.Unsupported;
	}

	private static IReadOnlyList<ScreenEntry> SelectScreens(IReadOnlyList<ScreenEntry> screens, bool duplicated, AdaptiveCaptureProfile profile)
	{
		if (duplicated || profile.PrimaryOnly || screens.Count == 1)
		{
			int num = screens.ToList().FindIndex((ScreenEntry s) => s.Screen.Primary);
			return new _003C_003Ez__ReadOnlySingleElementList<ScreenEntry>((num >= 0) ? screens[num] : screens[0]);
		}
		return screens.Take(2).ToArray();
	}

	private static CaptureDisplaySettings BuildDisplaySettings(ScreenEntry entry, int logicalIndex, AdaptiveCaptureProfile profile)
	{
		Rectangle nativeBounds = NativeBoundsForScreen(entry.Screen);
		CaptureDisplayRole captureDisplayRole = ((logicalIndex != 0) ? CaptureDisplayRole.Secondary : CaptureDisplayRole.Primary);
		int captureFps = ((captureDisplayRole == CaptureDisplayRole.Primary) ? profile.PrimaryFps : profile.SecondaryFps);
		Size size = ((captureDisplayRole == CaptureDisplayRole.Secondary && profile.SecondaryDownscaleTo1080Floor) ? DownscaleToward1080Floor(nativeBounds.Size) : EvenSize(nativeBounds.Size));
		bool flag = size.Width != EvenDimension(nativeBounds.Width) || size.Height != EvenDimension(nativeBounds.Height);
		return new CaptureDisplaySettings(logicalIndex, entry.OriginalIndex, captureDisplayRole, entry.Screen, nativeBounds, size.Width, size.Height, captureFps, flag, flag ? "secondary_1080_floor" : "native");
	}

	internal static Size DownscaleToward1080Floor(Size native)
	{
		int num = EvenDimension(native.Width);
		int num2 = EvenDimension(native.Height);
		int num3 = Math.Min(num, num2);
		if (num3 <= 1080)
		{
			return new Size(num, num2);
		}
		double num4 = 1080.0 / (double)num3;
		return EvenSize(new Size(Math.Max(2, (int)Math.Round((double)num * num4)), Math.Max(2, (int)Math.Round((double)num2 * num4))));
	}

	private static Size EvenSize(Size size)
	{
		return new Size(EvenDimension(size.Width), EvenDimension(size.Height));
	}

	private static int EvenDimension(int value)
	{
		return Math.Max(2, value & -2);
	}

	private static Rectangle NativeBoundsForScreen(Screen screen)
	{
		try
		{
			return DisplayCapture.ResolveNativeBoundsForScreen(screen);
		}
		catch (Exception ex)
		{
			if (!_nativeBoundsFallbackLogged)
			{
				_nativeBoundsFallbackLogged = true;
				KuroLog.Capture("Native bounds resolve failed; fallback=virtualized_screen_bounds: " + ex.Message, "NativeBoundsForScreen", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\RecorderCore\\Capture\\DisplayTopologyPolicy.cs", 238);
			}
			return screen.Bounds;
		}
	}

	private static bool IsDuplicatedTopology(IReadOnlyList<ScreenEntry> screens)
	{
		if (TryGetActiveDisplayConfig(out DisplayConfigPathInfo[] paths) && paths.Length > 1)
		{
			return (from p in paths
				group p by $"{p.sourceInfo.adapterId.HighPart}:{p.sourceInfo.adapterId.LowPart}:{p.sourceInfo.id}").Any((IGrouping<string, DisplayConfigPathInfo> g) => g.Count() > 1);
		}
		if (screens.Count <= 1)
		{
			return false;
		}
		return screens.Select((ScreenEntry s) => s.Screen.Bounds).Distinct().Count() == 1;
	}

	private static int? TryGetActivePathCount()
	{
		if (!TryGetActiveDisplayConfig(out DisplayConfigPathInfo[] paths))
		{
			return null;
		}
		return paths.Length;
	}

	private static string BuildTopologySignature(IReadOnlyList<ScreenEntry> screens, int physicalCount, bool duplicated)
	{
		StringBuilder stringBuilder = new StringBuilder();
		stringBuilder.Append("physical=").Append(physicalCount).Append(";duplicated=")
			.Append(duplicated ? "1" : "0");
		foreach (ScreenEntry screen in screens)
		{
			Rectangle bounds = screen.Screen.Bounds;
			stringBuilder.Append(";screen=").Append(screen.OriginalIndex).Append(':')
				.Append(screen.Screen.DeviceName)
				.Append(':')
				.Append(screen.Screen.Primary ? "primary" : "secondary")
				.Append(':')
				.Append(bounds.Left)
				.Append(',')
				.Append(bounds.Top)
				.Append(',')
				.Append(bounds.Width)
				.Append('x')
				.Append(bounds.Height);
		}
		return stringBuilder.ToString();
	}

	private static int EpochForSignature(string signature)
	{
		lock (TopologyEpochSync)
		{
			if (!string.Equals(_lastTopologySignature, signature, StringComparison.Ordinal))
			{
				_lastTopologySignature = signature;
				_topologyEpoch++;
			}
			return Math.Max(_topologyEpoch, 1);
		}
	}

	private static bool TryGetActiveDisplayConfig(out DisplayConfigPathInfo[] paths)
	{
		paths = Array.Empty<DisplayConfigPathInfo>();
		try
		{
			int displayConfigBufferSizes = GetDisplayConfigBufferSizes(QueryDisplayConfigFlags.OnlyActivePaths | QueryDisplayConfigFlags.VirtualModeAware, out var numPathArrayElements, out var numModeInfoArrayElements);
			if (displayConfigBufferSizes != 0 || numPathArrayElements == 0)
			{
				if (!_displayConfigProbeFailureLogged)
				{
					_displayConfigProbeFailureLogged = true;
					KuroLog.Capture($"QueryDisplayConfig probe returned win32={displayConfigBufferSizes} path_count={numPathArrayElements}; topology evaluation fallback=screen_bounds_heuristic", "TryGetActiveDisplayConfig", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\RecorderCore\\Capture\\DisplayTopologyPolicy.cs", 322);
				}
				return false;
			}
			DisplayConfigPathInfo[] array = new DisplayConfigPathInfo[numPathArrayElements];
			DisplayConfigModeInfo[] modeInfoArray = new DisplayConfigModeInfo[numModeInfoArrayElements];
			displayConfigBufferSizes = QueryDisplayConfig(QueryDisplayConfigFlags.OnlyActivePaths | QueryDisplayConfigFlags.VirtualModeAware, ref numPathArrayElements, array, ref numModeInfoArrayElements, modeInfoArray, IntPtr.Zero);
			if (displayConfigBufferSizes != 0)
			{
				if (!_displayConfigProbeFailureLogged)
				{
					_displayConfigProbeFailureLogged = true;
					KuroLog.Capture($"QueryDisplayConfig probe returned win32={displayConfigBufferSizes}; topology evaluation fallback=screen_bounds_heuristic", "TryGetActiveDisplayConfig", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\RecorderCore\\Capture\\DisplayTopologyPolicy.cs", 342);
				}
				return false;
			}
			paths = array.Take((int)numPathArrayElements).ToArray();
			_displayConfigProbeFailureLogged = false;
			return true;
		}
		catch (Exception ex)
		{
			if (!_displayConfigProbeFailureLogged)
			{
				_displayConfigProbeFailureLogged = true;
				KuroLog.Capture("QueryDisplayConfig probe threw reason=" + ex.GetType().Name + "; topology evaluation fallback=screen_bounds_heuristic", "TryGetActiveDisplayConfig", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\RecorderCore\\Capture\\DisplayTopologyPolicy.cs", 356);
			}
			paths = Array.Empty<DisplayConfigPathInfo>();
			return false;
		}
	}

	[DllImport("user32.dll")]
	private static extern int GetDisplayConfigBufferSizes(QueryDisplayConfigFlags flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);

	[DllImport("user32.dll")]
	private static extern int QueryDisplayConfig(QueryDisplayConfigFlags flags, ref uint numPathArrayElements, [Out] DisplayConfigPathInfo[] pathInfoArray, ref uint numModeInfoArrayElements, [Out] DisplayConfigModeInfo[] modeInfoArray, nint currentTopologyId);
}
