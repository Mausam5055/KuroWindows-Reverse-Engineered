using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace DeviceIdentity;

internal static class DisplayFacts
{
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

	[StructLayout(LayoutKind.Sequential, Size = 64)]
	private struct DisplayConfigModeInfo
	{
		public uint infoType;

		public uint id;

		public Luid adapterId;
	}

	private struct DisplayConfigDeviceInfoHeader
	{
		public uint type;

		public uint size;

		public Luid adapterId;

		public uint id;
	}

	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	private struct DisplayConfigTargetDeviceName
	{
		public DisplayConfigDeviceInfoHeader header;

		public uint flags;

		public int outputTechnology;

		public ushort edidManufactureId;

		public ushort edidProductCodeId;

		public uint connectorInstance;

		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
		public string monitorFriendlyDeviceName;

		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
		public string monitorDevicePath;
	}

	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	private struct DisplayConfigSourceDeviceName
	{
		public DisplayConfigDeviceInfoHeader header;

		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
		public string viewGdiDeviceName;
	}

	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	private struct DisplayDevice
	{
		public int cb;

		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
		public string DeviceName;

		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
		public string DeviceString;

		public uint StateFlags;

		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
		public string DeviceID;

		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
		public string DeviceKey;
	}

	public const int MaxDisplays = 4;

	private const int MaxAdapterChars = 48;

	private const uint QdcOnlyActivePaths = 2u;

	private const uint GetSourceName = 1u;

	private const uint GetTargetName = 2u;

	private const uint EdidIdsValid = 4u;

	private const int TechInternal = int.MinValue;

	private const int TechLvds = 6;

	private const int TechDisplayPortEmbedded = 11;

	public static IReadOnlyList<DisplayFact> Enumerate()
	{
		try
		{
			if (!TryGetActivePaths(out DisplayConfigPathInfo[] paths) || paths.Length == 0)
			{
				return Array.Empty<DisplayFact>();
			}
			Dictionary<string, string> adapters = ReadAdapterDescriptions();
			Lazy<HashSet<string>> serials = new Lazy<HashSet<string>>(ReadMonitorsWithSerial, LazyThreadSafetyMode.ExecutionAndPublication);
			HashSet<(uint, int, uint)> hashSet = new HashSet<(uint, int, uint)>();
			List<DisplayFact> list = new List<DisplayFact>(4);
			DisplayConfigPathInfo[] array = paths;
			for (int i = 0; i < array.Length; i++)
			{
				DisplayConfigPathInfo path = array[i];
				if (list.Count >= 4)
				{
					break;
				}
				if (hashSet.Add((path.targetInfo.adapterId.LowPart, path.targetInfo.adapterId.HighPart, path.targetInfo.id)))
				{
					list.Add(Describe(path, adapters, serials));
				}
			}
			return list;
		}
		catch
		{
			return Array.Empty<DisplayFact>();
		}
	}

	private static DisplayFact Describe(DisplayConfigPathInfo path, Dictionary<string, string> adapters, Lazy<HashSet<string>?> serials)
	{
		string tech = null;
		string text = null;
		string text2 = null;
		bool builtin = false;
		bool serial = false;
		string adapter = null;
		try
		{
			DisplayConfigTargetDeviceName requestPacket = new DisplayConfigTargetDeviceName
			{
				header = new DisplayConfigDeviceInfoHeader
				{
					type = 2u,
					size = (uint)Marshal.SizeOf<DisplayConfigTargetDeviceName>(),
					adapterId = path.targetInfo.adapterId,
					id = path.targetInfo.id
				}
			};
			if (DisplayConfigGetDeviceInfo(ref requestPacket) == 0)
			{
				tech = TechName(requestPacket.outputTechnology);
				int outputTechnology = requestPacket.outputTechnology;
				bool flag = ((outputTechnology == int.MinValue || outputTechnology == 6 || outputTechnology == 11) ? true : false);
				builtin = flag;
				if ((requestPacket.flags & 4) != 0)
				{
					text = DecodePnpId(requestPacket.edidManufactureId);
					text2 = requestPacket.edidProductCodeId.ToString("X4");
					if (text != null)
					{
						HashSet<string> value = serials.Value;
						if (value != null)
						{
							serial = value.Contains(text + text2);
						}
					}
				}
			}
			else
			{
				tech = TechName(path.targetInfo.outputTechnology);
				int outputTechnology = path.targetInfo.outputTechnology;
				bool flag = ((outputTechnology == int.MinValue || outputTechnology == 6 || outputTechnology == 11) ? true : false);
				builtin = flag;
			}
		}
		catch
		{
		}
		try
		{
			DisplayConfigSourceDeviceName requestPacket2 = new DisplayConfigSourceDeviceName
			{
				header = new DisplayConfigDeviceInfoHeader
				{
					type = 1u,
					size = (uint)Marshal.SizeOf<DisplayConfigSourceDeviceName>(),
					adapterId = path.sourceInfo.adapterId,
					id = path.sourceInfo.id
				}
			};
			if (DisplayConfigGetDeviceInfo(ref requestPacket2) == 0 && !string.IsNullOrWhiteSpace(requestPacket2.viewGdiDeviceName) && adapters.TryGetValue(requestPacket2.viewGdiDeviceName, out string value2))
			{
				adapter = HardwareFacets.Text(value2, 48);
			}
		}
		catch
		{
		}
		return new DisplayFact(tech, text, text2, serial, adapter, builtin);
	}

	internal static string TechName(int technology)
	{
		return technology switch
		{
			-1 => "other", 
			0 => "hd15", 
			1 => "svideo", 
			2 => "composite", 
			3 => "component", 
			4 => "dvi", 
			5 => "hdmi", 
			6 => "lvds", 
			8 => "d_jpn", 
			9 => "sdi", 
			10 => "displayport_external", 
			11 => "displayport_embedded", 
			12 => "udi_external", 
			13 => "udi_embedded", 
			14 => "sdtvdongle", 
			15 => "miracast", 
			16 => "indirect_wired", 
			17 => "indirect_virtual", 
			18 => "displayport_usb_tunnel", 
			int.MinValue => "internal", 
			_ => $"t{technology}", 
		};
	}

	internal static string? DecodePnpId(ushort word)
	{
		return TryDecodePnpId((ushort)((word >> 8) | (word << 8))) ?? TryDecodePnpId(word);
	}

	private static string? TryDecodePnpId(ushort word)
	{
		if ((word & 0x8000) != 0)
		{
			return null;
		}
		int num = (word >> 10) & 0x1F;
		int num2 = (word >> 5) & 0x1F;
		int num3 = word & 0x1F;
		bool flag = ((num < 1 || num > 26) ? true : false);
		bool flag2 = flag;
		if (!flag2)
		{
			bool flag3 = ((num2 < 1 || num2 > 26) ? true : false);
			flag2 = flag3;
		}
		bool flag4 = flag2;
		if (!flag4)
		{
			bool flag3 = ((num3 < 1 || num3 > 26) ? true : false);
			flag4 = flag3;
		}
		if (flag4)
		{
			return null;
		}
		_003C_003Ey__InlineArray3<char> buffer = default;
		buffer[0] = (char)(65 + num - 1);
		buffer[1] = (char)(65 + num2 - 1);
		buffer[2] = (char)(65 + num3 - 1);
		return new string(buffer);
	}

	private static Dictionary<string, string> ReadAdapterDescriptions()
	{
		Dictionary<string, string> dictionary = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		try
		{
			for (uint num = 0u; num < 32; num++)
			{
				DisplayDevice displayDevice = new DisplayDevice
				{
					cb = Marshal.SizeOf<DisplayDevice>()
				};
				if (EnumDisplayDevicesW(null, num, ref displayDevice, 0u))
				{
					if (!string.IsNullOrWhiteSpace(displayDevice.DeviceName) && !string.IsNullOrWhiteSpace(displayDevice.DeviceString))
					{
						dictionary.TryAdd(displayDevice.DeviceName, displayDevice.DeviceString);
					}
					continue;
				}
				break;
			}
		}
		catch
		{
		}
		return dictionary;
	}

	private static HashSet<string>? ReadMonitorsWithSerial()
	{
		try
		{
			HashSet<string> hashSet = new HashSet<string>(StringComparer.Ordinal);
			using ManagementObjectSearcher managementObjectSearcher = new ManagementObjectSearcher("root\\wmi", "SELECT ManufacturerName, ProductCodeID, SerialNumberID FROM WmiMonitorID");
			foreach (ManagementObject item in managementObjectSearcher.Get())
			{
				using (item)
				{
					string text = Decode(item["ManufacturerName"]);
					string text2 = Decode(item["ProductCodeID"]);
					string text3 = Decode(item["SerialNumberID"]);
					if (text.Length != 0 && text2.Length != 0 && text3.Length > 0 && text3.Any((char ch) => ch != '0'))
					{
						hashSet.Add(text + text2.ToUpperInvariant());
					}
				}
			}
			return hashSet;
		}
		catch
		{
			return null;
		}
	}

	private static string Decode(object? value)
	{
		if (!(value is Array array))
		{
			return "";
		}
		StringBuilder stringBuilder = new StringBuilder(array.Length);
		foreach (object item in array)
		{
			int num = Convert.ToInt32(item);
			switch (num)
			{
			case 0:
				break;
			case 32:
			case 33:
			case 34:
			case 35:
			case 36:
			case 37:
			case 38:
			case 39:
			case 40:
			case 41:
			case 42:
			case 43:
			case 44:
			case 45:
			case 46:
			case 47:
			case 48:
			case 49:
			case 50:
			case 51:
			case 52:
			case 53:
			case 54:
			case 55:
			case 56:
			case 57:
			case 58:
			case 59:
			case 60:
			case 61:
			case 62:
			case 63:
			case 64:
			case 65:
			case 66:
			case 67:
			case 68:
			case 69:
			case 70:
			case 71:
			case 72:
			case 73:
			case 74:
			case 75:
			case 76:
			case 77:
			case 78:
			case 79:
			case 80:
			case 81:
			case 82:
			case 83:
			case 84:
			case 85:
			case 86:
			case 87:
			case 88:
			case 89:
			case 90:
			case 91:
			case 92:
			case 93:
			case 94:
			case 95:
			case 96:
			case 97:
			case 98:
			case 99:
			case 100:
			case 101:
			case 102:
			case 103:
			case 104:
			case 105:
			case 106:
			case 107:
			case 108:
			case 109:
			case 110:
			case 111:
			case 112:
			case 113:
			case 114:
			case 115:
			case 116:
			case 117:
			case 118:
			case 119:
			case 120:
			case 121:
			case 122:
			case 123:
			case 124:
			case 125:
			case 126:
				stringBuilder.Append((char)num);
				continue;
			default:
				continue;
			}
			break;
		}
		return stringBuilder.ToString().Trim();
	}

	private static bool TryGetActivePaths(out DisplayConfigPathInfo[] paths)
	{
		paths = Array.Empty<DisplayConfigPathInfo>();
		if (GetDisplayConfigBufferSizes(2u, out var numPathArrayElements, out var numModeInfoArrayElements) != 0 || numPathArrayElements == 0)
		{
			return false;
		}
		DisplayConfigPathInfo[] array = new DisplayConfigPathInfo[numPathArrayElements];
		DisplayConfigModeInfo[] modeInfoArray = new DisplayConfigModeInfo[numModeInfoArrayElements];
		if (QueryDisplayConfig(2u, ref numPathArrayElements, array, ref numModeInfoArrayElements, modeInfoArray, IntPtr.Zero) != 0)
		{
			return false;
		}
		paths = array.Take((int)numPathArrayElements).ToArray();
		return true;
	}

	[DllImport("user32.dll")]
	private static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);

	[DllImport("user32.dll")]
	private static extern int QueryDisplayConfig(uint flags, ref uint numPathArrayElements, [Out] DisplayConfigPathInfo[] pathInfoArray, ref uint numModeInfoArrayElements, [Out] DisplayConfigModeInfo[] modeInfoArray, nint currentTopologyId);

	[DllImport("user32.dll")]
	private static extern int DisplayConfigGetDeviceInfo(ref DisplayConfigTargetDeviceName requestPacket);

	[DllImport("user32.dll")]
	private static extern int DisplayConfigGetDeviceInfo(ref DisplayConfigSourceDeviceName requestPacket);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool EnumDisplayDevicesW(string? device, uint deviceIndex, ref DisplayDevice displayDevice, uint flags);
}
