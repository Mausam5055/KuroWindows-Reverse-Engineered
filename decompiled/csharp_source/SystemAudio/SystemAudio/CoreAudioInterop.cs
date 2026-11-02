using System;
using System.Runtime.InteropServices;

namespace SystemAudio;

internal static class CoreAudioInterop
{
	[StructLayout(LayoutKind.Sequential, Pack = 2)]
	public struct WaveFormatEx
	{
		public ushort FormatTag;

		public ushort Channels;

		public uint SamplesPerSec;

		public uint AvgBytesPerSec;

		public ushort BlockAlign;

		public ushort BitsPerSample;

		public ushort Size;

		public static WaveFormatEx Pcm48kStereo16 => new WaveFormatEx
		{
			FormatTag = 1,
			Channels = 2,
			SamplesPerSec = 48000u,
			AvgBytesPerSec = 192000u,
			BlockAlign = 4,
			BitsPerSample = 16,
			Size = 0
		};
	}

	[StructLayout(LayoutKind.Sequential, Pack = 2)]
	public struct WaveFormatExtensibleTail
	{
		public ushort ValidBitsPerSample;

		public uint ChannelMask;

		public Guid SubFormat;
	}

	public static readonly Guid IidAudioClient = new Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");

	public static readonly Guid IidAudioCaptureClient = new Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317");

	public static readonly Guid IidAudioRenderClient = new Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2");

	public static readonly Guid SubtypePcm = new Guid("00000001-0000-0010-8000-00aa00389b71");

	public static readonly Guid SubtypeIeeeFloat = new Guid("00000003-0000-0010-8000-00aa00389b71");

	public const int ShareModeShared = 0;

	public const uint StreamFlagsLoopback = 131072u;

	public const uint StreamFlagsEventCallback = 262144u;

	public const uint StreamFlagsAutoConvertPcm = 2147483648u;

	public const uint StreamFlagsSrcDefaultQuality = 134217728u;

	public const uint BufferFlagsDataDiscontinuity = 1u;

	public const uint BufferFlagsSilent = 2u;

	public const uint BufferFlagsTimestampError = 4u;

	public const int DeviceInvalidated = -2004287484;

	public const int ResourcesInvalidated = -2004287450;

	public const int ServiceNotRunning = -2004287472;

	public const int UnsupportedFormat = -2004287480;

	public const int BufferError = -2004287464;

	public const int BufferEmpty = 143196161;

	public const int NotFound = -2147023728;

	public const int ClsCtxAll = 23;

	public const int DeviceStateActive = 1;

	public const uint WaitObject0 = 0u;

	public const uint WaitTimeout = 258u;

	public const uint WaitFailed = uint.MaxValue;

	public const uint CoInitMultithreaded = 0u;

	public const int RpcChangedMode = -2147417850;

	public const ushort FormatTagPcm = 1;

	public const ushort FormatTagIeeeFloat = 3;

	public const ushort FormatTagExtensible = 65534;

	public static bool IsInvalidated(int hr)
	{
		if (hr != -2004287484)
		{
			return hr == -2004287450;
		}
		return true;
	}

	[DllImport("ole32.dll", ExactSpelling = true)]
	public static extern int CoInitializeEx(nint reserved, uint coInit);

	[DllImport("ole32.dll", ExactSpelling = true)]
	public static extern void CoUninitialize();

	[DllImport("kernel32.dll", ExactSpelling = true)]
	public static extern nint CreateEventW(nint eventAttributes, [MarshalAs(UnmanagedType.Bool)] bool manualReset, [MarshalAs(UnmanagedType.Bool)] bool initialState, nint name);

	[DllImport("kernel32.dll", ExactSpelling = true)]
	public static extern uint WaitForSingleObject(nint handle, uint milliseconds);

	[DllImport("kernel32.dll", ExactSpelling = true)]
	public unsafe static extern uint WaitForMultipleObjects(uint count, nint* handles, [MarshalAs(UnmanagedType.Bool)] bool waitAll, uint milliseconds);

	[DllImport("kernel32.dll", ExactSpelling = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static extern bool SetEvent(nint handle);

	[DllImport("kernel32.dll", ExactSpelling = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static extern bool CloseHandle(nint handle);

	[DllImport("avrt.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
	public static extern nint AvSetMmThreadCharacteristicsW(string taskName, ref uint taskIndex);

	[DllImport("avrt.dll", ExactSpelling = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static extern bool AvRevertMmThreadCharacteristics(nint handle);
}
