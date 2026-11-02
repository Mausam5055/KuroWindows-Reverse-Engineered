using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Vortice;
using Vortice.DXGI;

namespace KuroWindows.Capture;

internal static class DxgiDisplayResolver
{
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

	private const uint DisplayConfigModeInfoTypeSource = 1u;

	public static DxgiDisplayMatch Score(Screen screen, OutputDescription output, long adapterLuid, long? displayConfigLuid)
	{
		bool flag = string.Equals(output.DeviceName, screen.DeviceName, StringComparison.OrdinalIgnoreCase);
		bool flag2 = BoundsMatch(screen.Bounds, DesktopBounds(output));
		bool flag3 = displayConfigLuid.HasValue && adapterLuid == displayConfigLuid.Value;
		if (!flag && !flag2)
		{
			return new DxgiDisplayMatch(0, "no_match", DeviceNameMatches: false, BoundsMatch: false, flag3);
		}
		int num = 0;
		List<string> list = new List<string>(3);
		if (flag)
		{
			num += 100;
			list.Add("device_name");
		}
		if (flag2)
		{
			num += 80;
			list.Add("bounds");
		}
		if (flag3)
		{
			num += 60;
			list.Add("display_config_luid");
		}
		return new DxgiDisplayMatch(num, string.Join("+", list), flag, flag2, flag3);
	}

	public static bool Matches(Screen screen, OutputDescription output, long adapterLuid, long? displayConfigLuid)
	{
		return Score(screen, output, adapterLuid, displayConfigLuid).Score > 0;
	}

	public static long AdapterLuidToInt64(uint lowPart, int highPart)
	{
		return ((long)highPart << 32) | lowPart;
	}

	public static Rectangle DesktopBounds(OutputDescription output)
	{
		RawRect desktopCoordinates = output.DesktopCoordinates;
		return Rectangle.FromLTRB(desktopCoordinates.Left, desktopCoordinates.Top, desktopCoordinates.Right, desktopCoordinates.Bottom);
	}

	public static long? TryGetDisplayConfigAdapterLuid(Screen screen)
	{
		try
		{
			if (GetDisplayConfigBufferSizes(QueryDisplayConfigFlags.OnlyActivePaths | QueryDisplayConfigFlags.VirtualModeAware, out var numPathArrayElements, out var numModeInfoArrayElements) != 0 || numPathArrayElements == 0 || numModeInfoArrayElements == 0)
			{
				return null;
			}
			DisplayConfigPathInfo[] array = new DisplayConfigPathInfo[numPathArrayElements];
			DisplayConfigModeInfo[] array2 = new DisplayConfigModeInfo[numModeInfoArrayElements];
			if (QueryDisplayConfig(QueryDisplayConfigFlags.OnlyActivePaths | QueryDisplayConfigFlags.VirtualModeAware, ref numPathArrayElements, array, ref numModeInfoArrayElements, array2, IntPtr.Zero) != 0)
			{
				return null;
			}
			Rectangle bounds = screen.Bounds;
			for (int i = 0; i < numPathArrayElements; i++)
			{
				DisplayConfigPathSourceInfo sourceInfo = array[i].sourceInfo;
				if (TryFindSourceMode(array2, numModeInfoArrayElements, sourceInfo, out var sourceMode))
				{
					Rectangle rectangle = checked(new Rectangle(sourceMode.position.x, sourceMode.position.y, (int)sourceMode.width, (int)sourceMode.height));
					if (rectangle.Left == bounds.Left && rectangle.Top == bounds.Top && rectangle.Right == bounds.Right && rectangle.Bottom == bounds.Bottom)
					{
						return AdapterLuidToInt64(sourceInfo.adapterId.LowPart, sourceInfo.adapterId.HighPart);
					}
				}
			}
		}
		catch (Exception ex)
		{
			KuroLog.Capture("QueryDisplayConfig LUID probe skipped for " + screen.DeviceName + ": " + ex.GetType().Name, "TryGetDisplayConfigAdapterLuid", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\DxgiDisplayResolver.cs", 110);
		}
		return null;
	}

	private static bool BoundsMatch(Rectangle screenBounds, Rectangle desktopBounds)
	{
		if (screenBounds.Left == desktopBounds.Left && screenBounds.Top == desktopBounds.Top && screenBounds.Right == desktopBounds.Right)
		{
			return screenBounds.Bottom == desktopBounds.Bottom;
		}
		return false;
	}

	private static bool TryFindSourceMode(DisplayConfigModeInfo[] modes, uint modeCount, DisplayConfigPathSourceInfo source, out DisplayConfigSourceMode sourceMode)
	{
		for (int i = 0; i < modeCount; i++)
		{
			DisplayConfigModeInfo displayConfigModeInfo = modes[i];
			if (displayConfigModeInfo.infoType == 1 && displayConfigModeInfo.id == source.id && displayConfigModeInfo.adapterId.LowPart == source.adapterId.LowPart && displayConfigModeInfo.adapterId.HighPart == source.adapterId.HighPart)
			{
				sourceMode = displayConfigModeInfo.modeInfo.sourceMode;
				return true;
			}
		}
		sourceMode = default;
		return false;
	}

	[DllImport("user32.dll")]
	private static extern int GetDisplayConfigBufferSizes(QueryDisplayConfigFlags flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);

	[DllImport("user32.dll")]
	private static extern int QueryDisplayConfig(QueryDisplayConfigFlags flags, ref uint numPathArrayElements, [Out] DisplayConfigPathInfo[] pathInfoArray, ref uint numModeInfoArrayElements, [Out] DisplayConfigModeInfo[] modeInfoArray, nint currentTopologyId);
}
