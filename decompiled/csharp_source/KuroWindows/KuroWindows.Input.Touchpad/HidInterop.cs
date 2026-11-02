using System;
using System.Runtime.InteropServices;

namespace KuroWindows.Input.Touchpad;

internal static class HidInterop
{
	public struct RAWINPUTDEVICE
	{
		public ushort usUsagePage;

		public ushort usUsage;

		public uint dwFlags;

		public nint hwndTarget;
	}

	public struct RAWINPUTHEADER
	{
		public uint dwType;

		public uint dwSize;

		public nint hDevice;

		public nint wParam;
	}

	public struct RAWINPUTDEVICELIST
	{
		public nint hDevice;

		public uint dwType;
	}

	[StructLayout(LayoutKind.Explicit, Size = 64)]
	public struct HIDP_CAPS
	{
		[FieldOffset(0)]
		public ushort Usage;

		[FieldOffset(2)]
		public ushort UsagePage;

		[FieldOffset(4)]
		public ushort InputReportByteLength;

		[FieldOffset(6)]
		public ushort OutputReportByteLength;

		[FieldOffset(8)]
		public ushort FeatureReportByteLength;

		[FieldOffset(44)]
		public ushort NumberLinkCollectionNodes;

		[FieldOffset(46)]
		public ushort NumberInputButtonCaps;

		[FieldOffset(48)]
		public ushort NumberInputValueCaps;

		[FieldOffset(50)]
		public ushort NumberInputDataIndices;
	}

	[StructLayout(LayoutKind.Explicit, Size = 24)]
	public struct HIDP_LINK_COLLECTION_NODE
	{
		[FieldOffset(0)]
		public ushort LinkUsage;

		[FieldOffset(2)]
		public ushort LinkUsagePage;

		[FieldOffset(4)]
		public ushort Parent;

		[FieldOffset(6)]
		public ushort NumberOfChildren;

		[FieldOffset(8)]
		public ushort NextSibling;

		[FieldOffset(10)]
		public ushort FirstChild;

		[FieldOffset(12)]
		public uint Bits;

		[FieldOffset(16)]
		public nint UserContext;
	}

	[StructLayout(LayoutKind.Explicit, Size = 72)]
	public struct HIDP_VALUE_CAPS
	{
		[FieldOffset(0)]
		public ushort UsagePage;

		[FieldOffset(2)]
		public byte ReportID;

		[FieldOffset(3)]
		public byte IsAlias;

		[FieldOffset(4)]
		public ushort BitField;

		[FieldOffset(6)]
		public ushort LinkCollection;

		[FieldOffset(8)]
		public ushort LinkUsage;

		[FieldOffset(10)]
		public ushort LinkUsagePage;

		[FieldOffset(12)]
		public byte IsRange;

		[FieldOffset(13)]
		public byte IsStringRange;

		[FieldOffset(14)]
		public byte IsDesignatorRange;

		[FieldOffset(15)]
		public byte IsAbsolute;

		[FieldOffset(16)]
		public byte HasNull;

		[FieldOffset(18)]
		public ushort BitSize;

		[FieldOffset(20)]
		public ushort ReportCount;

		[FieldOffset(32)]
		public uint UnitsExp;

		[FieldOffset(36)]
		public uint Units;

		[FieldOffset(40)]
		public int LogicalMin;

		[FieldOffset(44)]
		public int LogicalMax;

		[FieldOffset(48)]
		public int PhysicalMin;

		[FieldOffset(52)]
		public int PhysicalMax;

		[FieldOffset(56)]
		public ushort UsageMin;

		[FieldOffset(58)]
		public ushort UsageMax;

		[FieldOffset(68)]
		public ushort DataIndexMin;

		[FieldOffset(70)]
		public ushort DataIndexMax;
	}

	[StructLayout(LayoutKind.Explicit, Size = 72)]
	public struct HIDP_BUTTON_CAPS
	{
		[FieldOffset(0)]
		public ushort UsagePage;

		[FieldOffset(2)]
		public byte ReportID;

		[FieldOffset(3)]
		public byte IsAlias;

		[FieldOffset(4)]
		public ushort BitField;

		[FieldOffset(6)]
		public ushort LinkCollection;

		[FieldOffset(8)]
		public ushort LinkUsage;

		[FieldOffset(10)]
		public ushort LinkUsagePage;

		[FieldOffset(12)]
		public byte IsRange;

		[FieldOffset(13)]
		public byte IsStringRange;

		[FieldOffset(14)]
		public byte IsDesignatorRange;

		[FieldOffset(15)]
		public byte IsAbsolute;

		[FieldOffset(16)]
		public ushort ReportCount;

		[FieldOffset(56)]
		public ushort UsageMin;

		[FieldOffset(58)]
		public ushort UsageMax;

		[FieldOffset(68)]
		public ushort DataIndexMin;

		[FieldOffset(70)]
		public ushort DataIndexMax;
	}

	public struct HIDP_DATA
	{
		public ushort DataIndex;

		public ushort Reserved;

		public uint RawValue;

		public bool On => (RawValue & 0xFF) != 0;
	}

	public readonly record struct RidDeviceInfo(uint Type, int VendorId, int ProductId, int VersionNumber, ushort UsagePage, ushort Usage);

	public const int WM_INPUT = 255;

	public const int WM_INPUT_DEVICE_CHANGE = 254;

	public const int GIDC_ARRIVAL = 1;

	public const int GIDC_REMOVAL = 2;

	public const uint RID_INPUT = 268435459u;

	public const uint RIDEV_INPUTSINK = 256u;

	public const uint RIDEV_DEVNOTIFY = 8192u;

	public const uint RIDEV_REMOVE = 1u;

	public const uint RIDI_PREPARSEDDATA = 536870917u;

	public const uint RIDI_DEVICENAME = 536870919u;

	public const uint RIDI_DEVICEINFO = 536870923u;

	public const uint RIM_TYPEMOUSE = 0u;

	public const uint RIM_TYPEKEYBOARD = 1u;

	public const uint RIM_TYPEHID = 2u;

	public const ushort HID_USAGE_PAGE_GENERIC = 1;

	public const ushort HID_USAGE_PAGE_BUTTON = 9;

	public const ushort HID_USAGE_PAGE_DIGITIZER = 13;

	public const ushort HID_USAGE_GENERIC_X = 48;

	public const ushort HID_USAGE_GENERIC_Y = 49;

	public const ushort HID_USAGE_GENERIC_MOUSE = 2;

	public const ushort HID_USAGE_GENERIC_JOYSTICK = 4;

	public const ushort HID_USAGE_GENERIC_GAMEPAD = 5;

	public const ushort HID_USAGE_GENERIC_KEYBOARD = 6;

	public const ushort HID_USAGE_DIGITIZER_PEN = 2;

	public const ushort HID_USAGE_DIGITIZER_TOUCH_SCREEN = 4;

	public const ushort HID_USAGE_DIGITIZER_TOUCH_PAD = 5;

	public const ushort HID_USAGE_DIGITIZER_FINGER = 34;

	public const ushort HID_USAGE_DIGITIZER_TIP_PRESSURE = 48;

	public const ushort HID_USAGE_DIGITIZER_TIP_SWITCH = 66;

	public const ushort HID_USAGE_DIGITIZER_CONFIDENCE = 71;

	public const ushort HID_USAGE_DIGITIZER_WIDTH = 72;

	public const ushort HID_USAGE_DIGITIZER_HEIGHT = 73;

	public const ushort HID_USAGE_DIGITIZER_CONTACT_ID = 81;

	public const ushort HID_USAGE_DIGITIZER_CONTACT_COUNT = 84;

	public const ushort HID_USAGE_DIGITIZER_SCAN_TIME = 86;

	public const int ERROR_INSUFFICIENT_BUFFER = 122;

	public const int HidP_Input = 0;

	public const int HIDP_STATUS_SUCCESS = 1114112;

	public const int RidDeviceInfoSize = 32;

	public const int RawHidSizeOffset = 24;

	public const int RawHidCountOffset = 28;

	public const int RawHidDataOffset = 32;

	public const int RawInputBufferInitialBytes = 1024;

	public const int RawInputBufferMaxBytes = 65536;

	[DllImport("user32.dll", SetLastError = true)]
	public static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] devices, uint num, uint size);

	[DllImport("user32.dll", SetLastError = true)]
	public static extern uint GetRegisteredRawInputDevices([Out] RAWINPUTDEVICE[]? devices, ref uint num, uint size);

	[DllImport("user32.dll", SetLastError = true)]
	public static extern uint GetRawInputDeviceList([Out] RAWINPUTDEVICELIST[]? list, ref uint num, uint size);

	[DllImport("user32.dll", SetLastError = true)]
	public static extern uint GetRawInputDeviceInfoW(nint hDevice, uint command, nint data, ref uint size);

	[DllImport("user32.dll", SetLastError = true)]
	public static extern uint GetRawInputData(nint hRawInput, uint cmd, nint data, ref uint size, uint headerSize);

	[DllImport("hid.dll")]
	public static extern int HidP_GetCaps(nint preparsedData, out HIDP_CAPS caps);

	[DllImport("hid.dll")]
	public static extern int HidP_GetLinkCollectionNodes([Out] HIDP_LINK_COLLECTION_NODE[] nodes, ref uint length, nint preparsedData);

	[DllImport("hid.dll")]
	public static extern int HidP_GetValueCaps(int reportType, [Out] HIDP_VALUE_CAPS[] caps, ref ushort length, nint preparsedData);

	[DllImport("hid.dll")]
	public static extern int HidP_GetButtonCaps(int reportType, [Out] HIDP_BUTTON_CAPS[] caps, ref ushort length, nint preparsedData);

	[DllImport("hid.dll")]
	public static extern uint HidP_MaxDataListLength(int reportType, nint preparsedData);

	[DllImport("hid.dll")]
	public static extern int HidP_GetData(int reportType, [In][Out] HIDP_DATA[] dataList, ref uint dataLength, nint preparsedData, nint report, uint reportLength);

	public static bool TryDeviceInfo(nint hDevice, out RidDeviceInfo info)
	{
		info = default;
		nint num = Marshal.AllocHGlobal(32);
		bool flag;
		try
		{
			Marshal.WriteInt32(num, 0, 32);
			uint size = 32u;
			uint rawInputDeviceInfoW = GetRawInputDeviceInfoW(hDevice, 536870923u, num, ref size);
			flag = ((rawInputDeviceInfoW == 0 || rawInputDeviceInfoW == uint.MaxValue) ? true : false);
			if (flag)
			{
				flag = false;
			}
			else
			{
				uint num2 = (uint)Marshal.ReadInt32(num, 4);
				info = ((num2 == 2) ? new RidDeviceInfo(num2, Marshal.ReadInt32(num, 8), Marshal.ReadInt32(num, 12), Marshal.ReadInt32(num, 16), (ushort)Marshal.ReadInt16(num, 20), (ushort)Marshal.ReadInt16(num, 22)) : new RidDeviceInfo(num2, 0, 0, 0, 0, 0));
				flag = true;
			}
		}
		finally
		{
			Marshal.FreeHGlobal(num);
		}
		return flag;
	}

	public static string? DeviceName(nint hDevice)
	{
		uint size = 0u;
		GetRawInputDeviceInfoW(hDevice, 536870919u, IntPtr.Zero, ref size);
		if ((size > 1024 || size == 0) ? true : false)
		{
			return null;
		}
		nint num = Marshal.AllocHGlobal((int)(size * 2));
		try
		{
			uint rawInputDeviceInfoW = GetRawInputDeviceInfoW(hDevice, 536870919u, num, ref size);
			if ((rawInputDeviceInfoW == 0 || rawInputDeviceInfoW == uint.MaxValue) ? true : false)
			{
				return null;
			}
			return Marshal.PtrToStringUni(num, (int)Math.Min(rawInputDeviceInfoW, size))?.TrimEnd('\0');
		}
		finally
		{
			Marshal.FreeHGlobal(num);
		}
	}

	public static nint ReadPreparsedData(nint hDevice, out int bytes)
	{
		bytes = 0;
		uint size = 0u;
		GetRawInputDeviceInfoW(hDevice, 536870917u, IntPtr.Zero, ref size);
		if ((size > 65536 || size == 0) ? true : false)
		{
			return IntPtr.Zero;
		}
		nint num = Marshal.AllocHGlobal((int)size);
		uint rawInputDeviceInfoW = GetRawInputDeviceInfoW(hDevice, 536870917u, num, ref size);
		if ((rawInputDeviceInfoW == 0 || rawInputDeviceInfoW == uint.MaxValue) ? true : false)
		{
			Marshal.FreeHGlobal(num);
			return IntPtr.Zero;
		}
		bytes = (int)rawInputDeviceInfoW;
		return num;
	}

	public static bool TryReadRawInput(nint hRawInput, ref nint buffer, ref uint bufferSize, out int growths)
	{
		growths = 0;
		uint size = bufferSize;
		uint headerSize = (uint)Marshal.SizeOf<RAWINPUTHEADER>();
		while (GetRawInputData(hRawInput, 268435459u, buffer, ref size, headerSize) == uint.MaxValue)
		{
			if (Marshal.GetLastWin32Error() != 122 || bufferSize >= 65536)
			{
				return false;
			}
			uint num = Math.Min(65536u, Math.Max(size, bufferSize * 2));
			buffer = Marshal.ReAllocHGlobal(buffer, (nint)num);
			bufferSize = num;
			size = num;
			growths++;
		}
		return true;
	}
}
