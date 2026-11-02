using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace KuroWindows.Input.Touchpad;

internal sealed class PtpDevicePlan : IDisposable
{
	private const double FallbackWidthMm = 69.0;

	private const double FallbackHeightMm = 50.0;

	private const double MinPadMm = 25.0;

	private const double MaxPadMm = 250.0;

	private const uint UnitsCentimetre = 17u;

	private const uint UnitsInch = 19u;

	public string Vid { get; private init; } = "";

	public string Pid { get; private init; } = "";

	public int Version { get; private init; }

	public string Iface { get; private init; } = "";

	public nint Preparsed { get; private set; }

	public int PreparsedBytes { get; private init; }

	public string PreparsedSha256 { get; private init; } = "";

	public string PreparsedBase64 { get; private init; } = "";

	public int ReportBytes { get; private init; }

	public int Slots { get; private init; }

	public byte ReportId { get; private init; }

	public HashSet<byte> ReportIds { get; private init; } = new HashSet<byte>();

	public PtpDataIndex[] Table { get; private init; } = Array.Empty<PtpDataIndex>();

	public uint MaxDataListLength { get; private init; }

	public bool HasConfidence { get; private init; }

	public bool HasContactCount { get; private init; }

	public bool HasScanTime { get; private init; }

	public int ScanTimeModulus { get; private init; } = 65535;

	public int LogicalXMin { get; private init; }

	public int LogicalXMax { get; private init; }

	public int LogicalYMin { get; private init; }

	public int LogicalYMax { get; private init; }

	public int PhysicalXMin { get; private init; }

	public int PhysicalXMax { get; private init; }

	public int PhysicalYMin { get; private init; }

	public int PhysicalYMax { get; private init; }

	public uint Units { get; private init; }

	public int UnitsExponent { get; private init; }

	public double MmPerCountX { get; private init; }

	public double MmPerCountY { get; private init; }

	public string MmPerCountSource { get; private init; } = "fallback";

	public double WidthMm { get; private init; }

	public double HeightMm { get; private init; }

	public string[] Usages { get; private init; } = Array.Empty<string>();

	public static PtpDevicePlan? TryBuild(nint hDevice, in HidInterop.RidDeviceInfo info)
	{
		nint num = HidInterop.ReadPreparsedData(hDevice, out var bytes);
		if (num == IntPtr.Zero)
		{
			return null;
		}
		try
		{
			if (HidInterop.HidP_GetCaps(num, out var caps) != 1114112 || caps.InputReportByteLength == 0)
			{
				return Fail(num);
			}
			Dictionary<ushort, byte> dictionary = new Dictionary<ushort, byte>();
			if (caps.NumberLinkCollectionNodes > 0)
			{
				HidInterop.HIDP_LINK_COLLECTION_NODE[] array = new HidInterop.HIDP_LINK_COLLECTION_NODE[caps.NumberLinkCollectionNodes];
				uint length = (uint)array.Length;
				if (HidInterop.HidP_GetLinkCollectionNodes(array, ref length, num) != 1114112)
				{
					return Fail(num);
				}
				for (ushort num2 = 0; num2 < length; num2++)
				{
					if (array[num2].LinkUsagePage == 13 && array[num2].LinkUsage == 34)
					{
						dictionary[num2] = (byte)dictionary.Count;
					}
				}
			}
			PtpDataIndex[] array2 = new PtpDataIndex[caps.NumberInputDataIndices];
			HashSet<byte> hashSet = new HashSet<byte>();
			List<string> list = new List<string>();
			bool hasContactCount = false;
			bool hasScanTime = false;
			bool hasConfidence = false;
			int scanTimeModulus = 65535;
			byte b = 0;
			HidInterop.HIDP_VALUE_CAPS? hIDP_VALUE_CAPS = null;
			HidInterop.HIDP_VALUE_CAPS? hIDP_VALUE_CAPS2 = null;
			if (caps.NumberInputValueCaps > 0)
			{
				HidInterop.HIDP_VALUE_CAPS[] array3 = new HidInterop.HIDP_VALUE_CAPS[caps.NumberInputValueCaps];
				ushort length2 = caps.NumberInputValueCaps;
				if (HidInterop.HidP_GetValueCaps(0, array3, ref length2, num) != 1114112)
				{
					return Fail(num);
				}
				for (int i = 0; i < length2; i++)
				{
					HidInterop.HIDP_VALUE_CAPS value = array3[i];
					byte b2 = (dictionary.TryGetValue(value.LinkCollection, out var value2) ? value2 : byte.MaxValue);
					int num3 = ((value.IsRange != 0) ? value.UsageMax : value.UsageMin);
					for (int j = value.UsageMin; j <= num3; j++)
					{
						PtpField ptpField = ValueField(value.UsagePage, (ushort)j, b2 != byte.MaxValue);
						if (ptpField == PtpField.None)
						{
							continue;
						}
						int num4 = value.DataIndexMin + (j - value.UsageMin);
						if (num4 >= array2.Length)
						{
							continue;
						}
						array2[num4] = new PtpDataIndex(b2, ptpField);
						Note(list, ptpField);
						if (b2 == 0)
						{
							if (ptpField == PtpField.X)
							{
								hIDP_VALUE_CAPS = value;
							}
							if (ptpField == PtpField.Y)
							{
								hIDP_VALUE_CAPS2 = value;
							}
							if (ptpField == PtpField.ContactId && b == 0)
							{
								b = value.ReportID;
							}
						}
						if (b2 != byte.MaxValue)
						{
							hashSet.Add(value.ReportID);
						}
						if (ptpField == PtpField.ContactCount)
						{
							hasContactCount = true;
							hashSet.Add(value.ReportID);
						}
						if (ptpField == PtpField.ScanTime)
						{
							hasScanTime = true;
							scanTimeModulus = Math.Max(1, Unsigned(value.LogicalMax, value.BitSize));
						}
					}
				}
			}
			if (caps.NumberInputButtonCaps > 0)
			{
				HidInterop.HIDP_BUTTON_CAPS[] array4 = new HidInterop.HIDP_BUTTON_CAPS[caps.NumberInputButtonCaps];
				ushort length3 = caps.NumberInputButtonCaps;
				if (HidInterop.HidP_GetButtonCaps(0, array4, ref length3, num) != 1114112)
				{
					return Fail(num);
				}
				for (int k = 0; k < length3; k++)
				{
					HidInterop.HIDP_BUTTON_CAPS hIDP_BUTTON_CAPS = array4[k];
					byte b3 = (dictionary.TryGetValue(hIDP_BUTTON_CAPS.LinkCollection, out var value3) ? value3 : byte.MaxValue);
					int num5 = ((hIDP_BUTTON_CAPS.IsRange != 0) ? hIDP_BUTTON_CAPS.UsageMax : hIDP_BUTTON_CAPS.UsageMin);
					for (int l = hIDP_BUTTON_CAPS.UsageMin; l <= num5; l++)
					{
						PtpField ptpField2 = ButtonField(hIDP_BUTTON_CAPS.UsagePage, (ushort)l, b3 != byte.MaxValue);
						if (ptpField2 == PtpField.None)
						{
							continue;
						}
						int num6 = hIDP_BUTTON_CAPS.DataIndexMin + (l - hIDP_BUTTON_CAPS.UsageMin);
						if (num6 < array2.Length)
						{
							array2[num6] = new PtpDataIndex(b3, ptpField2);
							Note(list, ptpField2);
							if (ptpField2 == PtpField.Confidence)
							{
								hasConfidence = true;
							}
						}
					}
				}
			}
			if (dictionary.Count == 0 || !hIDP_VALUE_CAPS.HasValue || !hIDP_VALUE_CAPS2.HasValue || !list.Contains("contactId"))
			{
				return Fail(num);
			}
			HidInterop.HIDP_VALUE_CAPS cap = hIDP_VALUE_CAPS.Value;
			HidInterop.HIDP_VALUE_CAPS cap2 = hIDP_VALUE_CAPS2.Value;
			int logicalMin = cap.LogicalMin;
			int num7 = Unsigned(cap.LogicalMax, cap.BitSize);
			int logicalMin2 = cap2.LogicalMin;
			int num8 = Unsigned(cap2.LogicalMax, cap2.BitSize);
			int num9 = DecodeExponent(cap.UnitsExp);
			double num10 = PhysicalMm(in cap, num9);
			double num11 = PhysicalMm(in cap2, DecodeExponent(cap2.UnitsExp));
			int num12 = Math.Max(1, num7 - logicalMin);
			int num13 = Math.Max(1, num8 - logicalMin2);
			bool flag = num10 >= 25.0 && num10 <= 250.0 && num11 >= 25.0 && num11 <= 250.0;
			if (!flag)
			{
				num10 = 69.0;
				num11 = 50.0;
			}
			byte[] array5 = new byte[bytes];
			Marshal.Copy(num, array5, 0, bytes);
			string s = HidInterop.DeviceName(hDevice) ?? "";
			return new PtpDevicePlan
			{
				Vid = (info.VendorId & 0xFFFF).ToString("X4"),
				Pid = (info.ProductId & 0xFFFF).ToString("X4"),
				Version = info.VersionNumber,
				Iface = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).Substring(0, 16).ToLowerInvariant(),
				Preparsed = num,
				PreparsedBytes = bytes,
				PreparsedSha256 = Convert.ToHexString(SHA256.HashData(array5)).ToLowerInvariant(),
				PreparsedBase64 = Convert.ToBase64String(array5),
				ReportBytes = caps.InputReportByteLength,
				Slots = dictionary.Count,
				ReportId = b,
				ReportIds = hashSet,
				Table = array2,
				MaxDataListLength = HidInterop.HidP_MaxDataListLength(0, num),
				HasConfidence = hasConfidence,
				HasContactCount = hasContactCount,
				HasScanTime = hasScanTime,
				ScanTimeModulus = scanTimeModulus,
				LogicalXMin = logicalMin,
				LogicalXMax = num7,
				LogicalYMin = logicalMin2,
				LogicalYMax = num8,
				PhysicalXMin = cap.PhysicalMin,
				PhysicalXMax = cap.PhysicalMax,
				PhysicalYMin = cap2.PhysicalMin,
				PhysicalYMax = cap2.PhysicalMax,
				Units = cap.Units,
				UnitsExponent = num9,
				MmPerCountX = num10 / (double)num12,
				MmPerCountY = num11 / (double)num13,
				MmPerCountSource = (flag ? "descriptor" : "fallback"),
				WidthMm = num10,
				HeightMm = num11,
				Usages = list.ToArray()
			};
		}
		catch
		{
			return Fail(num);
		}
	}

	private static PtpDevicePlan? Fail(nint preparsed)
	{
		Marshal.FreeHGlobal(preparsed);
		return null;
	}

	private static PtpField ValueField(ushort page, ushort usage, bool inSlot)
	{
		if ((page == 1) & inSlot)
		{
			return usage switch
			{
				48 => PtpField.X, 
				49 => PtpField.Y, 
				_ => PtpField.None, 
			};
		}
		if (page != 13)
		{
			return PtpField.None;
		}
		switch (usage)
		{
		case 81:
			if (inSlot)
			{
				return PtpField.ContactId;
			}
			break;
		case 72:
			if (inSlot)
			{
				return PtpField.Width;
			}
			break;
		case 73:
			if (inSlot)
			{
				return PtpField.Height;
			}
			break;
		case 48:
			if (inSlot)
			{
				return PtpField.Pressure;
			}
			break;
		case 86:
			if (!inSlot)
			{
				return PtpField.ScanTime;
			}
			break;
		case 84:
			if (!inSlot)
			{
				return PtpField.ContactCount;
			}
			break;
		}
		return PtpField.None;
	}

	private static PtpField ButtonField(ushort page, ushort usage, bool inSlot)
	{
		if ((page == 13) & inSlot)
		{
			return usage switch
			{
				66 => PtpField.Tip, 
				71 => PtpField.Confidence, 
				_ => PtpField.None, 
			};
		}
		if (page == 9 && !inSlot)
		{
			return usage switch
			{
				1 => PtpField.Button1, 
				2 => PtpField.Button2, 
				3 => PtpField.Button3, 
				_ => PtpField.None, 
			};
		}
		return PtpField.None;
	}

	private static void Note(List<string> usages, PtpField field)
	{
		string item = field switch
		{
			PtpField.ContactId => "contactId", 
			PtpField.X => "x", 
			PtpField.Y => "y", 
			PtpField.Tip => "tip", 
			PtpField.Confidence => "confidence", 
			PtpField.Width => "width", 
			PtpField.Height => "height", 
			PtpField.Pressure => "pressure", 
			PtpField.ScanTime => "scanTime", 
			PtpField.ContactCount => "contactCount", 
			PtpField.Button1 => "button1", 
			PtpField.Button2 => "button2", 
			_ => "button3", 
		};
		if (!usages.Contains(item))
		{
			usages.Add(item);
		}
	}

	private static int Unsigned(int value, int bitSize)
	{
		if (value >= 0 || bitSize <= 0 || bitSize >= 32)
		{
			return value;
		}
		return value & ((1 << bitSize) - 1);
	}

	private static int DecodeExponent(uint unitsExp)
	{
		if (unitsExp <= 15)
		{
			int num = (int)(unitsExp & 0xF);
			if (num <= 7)
			{
				return num;
			}
			return num - 16;
		}
		if ((int)unitsExp < -8 || (int)unitsExp > 7)
		{
			return 0;
		}
		return (int)unitsExp;
	}

	private static double PhysicalMm(in HidInterop.HIDP_VALUE_CAPS cap, int exponent)
	{
		double num = cap.Units switch
		{
			17u => 10.0, 
			19u => 25.4, 
			_ => 0.0, 
		};
		double num2 = (double)cap.PhysicalMax - (double)cap.PhysicalMin;
		if (num == 0.0 || num2 <= 0.0)
		{
			return 0.0;
		}
		return num2 * Math.Pow(10.0, exponent) * num;
	}

	public void Dispose()
	{
		if (Preparsed != IntPtr.Zero)
		{
			Marshal.FreeHGlobal(Preparsed);
			Preparsed = IntPtr.Zero;
		}
	}
}
