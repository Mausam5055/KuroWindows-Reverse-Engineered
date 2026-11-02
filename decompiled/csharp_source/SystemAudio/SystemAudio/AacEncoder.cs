using System;
using System.Text;
using System.Threading;

namespace SystemAudio;

internal sealed class AacEncoder : IDisposable
{
	public const int SampleRate = 48000;

	public const int Channels = 2;

	public const int FrameLength = 1024;

	public const int SamplesPerBlock = 2048;

	public const int Bitrate = 256000;

	public const int BandwidthHz = 20000;

	private const uint EncModulesAacCore = 1u;

	private const int PcmElementBytes = 2;

	private static readonly Lazy<string> LibraryVersion = new Lazy<string>(ReadVersion, LazyThreadSafetyMode.ExecutionAndPublication);

	private readonly short[] _flushPcm = new short[2048];

	private nint _handle;

	public int Delay { get; }

	public byte[] AudioSpecificConfig { get; } = Array.Empty<byte>();

	public int MaxOutBufBytes { get; }

	public string Version { get; }

	public unsafe AacEncoder()
	{
		try
		{
			Version = LibraryVersion.Value;
		}
		catch (Exception ex) when ((ex is DllNotFoundException || ex is BadImageFormatException || ex is EntryPointNotFoundException) ? true : false)
		{
			throw new AudioWriterOpenException("encoder_load", $"0x{ex.HResult:X8}", ex);
		}
		Check(FdkAacInterop.aacEncOpen(out _handle, 1u, 2u), "encoder_open");
		try
		{
			SetParam(AacencParam.AACENC_AOT, 2u);
			SetParam(AacencParam.AACENC_SAMPLERATE, 48000u);
			SetParam(AacencParam.AACENC_CHANNELMODE, 2u);
			SetParam(AacencParam.AACENC_CHANNELORDER, 1u);
			SetParam(AacencParam.AACENC_BITRATEMODE, 0u);
			SetParam(AacencParam.AACENC_BITRATE, 256000u);
			SetParam(AacencParam.AACENC_BANDWIDTH, 20000u);
			SetParam(AacencParam.AACENC_AFTERBURNER, 1u);
			SetParam(AacencParam.AACENC_TRANSMUX, 0u);
			SetParam(AacencParam.AACENC_SIGNALING_MODE, 0u);
			SetParam(AacencParam.AACENC_GRANULE_LENGTH, 1024u);
			Check(FdkAacInterop.aacEncEncode(_handle, null, null, null, null), "encoder_init");
			AacencInfoStruct aacencInfoStruct = default;
			Check(FdkAacInterop.aacEncInfo(_handle, &aacencInfoStruct), "encoder_info");
			if (aacencInfoStruct.FrameLength != 1024)
			{
				throw new AudioWriterOpenException("encoder_info", $"0x{64u:X4}");
			}
			Delay = (int)aacencInfoStruct.Delay;
			MaxOutBufBytes = (int)aacencInfoStruct.MaxOutBufBytes;
			int num = (int)Math.Min(aacencInfoStruct.ConfSize, 64u);
			AudioSpecificConfig = new byte[num];
			new ReadOnlySpan<byte>(aacencInfoStruct.ConfBuf, num).CopyTo(AudioSpecificConfig);
		}
		catch
		{
			Close();
			throw;
		}
	}

	public int Encode(ReadOnlySpan<short> pcm, Span<byte> accessUnit)
	{
		if (pcm.Length != 2048)
		{
			throw new ArgumentException($"Expected {2048} samples but received {pcm.Length}.", "pcm");
		}
		return Run(pcm, accessUnit, 2048);
	}

	public int Drain(Span<byte> accessUnit)
	{
		return Run(_flushPcm, accessUnit, -1);
	}

	public void Dispose()
	{
		Close();
	}

	private unsafe int Run(ReadOnlySpan<short> pcm, Span<byte> accessUnit, int numInSamples)
	{
		if (_handle == IntPtr.Zero)
		{
			throw new ObjectDisposedException("AacEncoder");
		}
		fixed (short* ptr = pcm)
		{
			fixed (byte* ptr2 = accessUnit)
			{
				void* ptr3 = ptr;
				int num = 0;
				int num2 = pcm.Length * 2;
				int num3 = 2;
				void* ptr4 = ptr2;
				int num4 = 3;
				int length = accessUnit.Length;
				int num5 = 1;
				AacencBufDesc aacencBufDesc = new AacencBufDesc
				{
					NumBufs = 1,
					Bufs = &ptr3,
					BufferIdentifiers = &num,
					BufSizes = &num2,
					BufElSizes = &num3
				};
				AacencBufDesc aacencBufDesc2 = new AacencBufDesc
				{
					NumBufs = 1,
					Bufs = &ptr4,
					BufferIdentifiers = &num4,
					BufSizes = &length,
					BufElSizes = &num5
				};
				AacencInArgs aacencInArgs = new AacencInArgs
				{
					NumInSamples = numInSamples,
					NumAncBytes = 0
				};
				AacencOutArgs aacencOutArgs = default;
				uint num6 = FdkAacInterop.aacEncEncode(_handle, &aacencBufDesc, &aacencBufDesc2, &aacencInArgs, &aacencOutArgs);
				return num6 switch
				{
					128u => 0, 
					0u => Math.Max(0, aacencOutArgs.NumOutBytes), 
					_ => throw new AacEncodeException(num6), 
				};
			}
		}
	}

	private void SetParam(AacencParam param, uint value)
	{
		Check(FdkAacInterop.aacEncoder_SetParam(_handle, param, value), "encoder_param");
	}

	private static void Check(uint error, string stage)
	{
		if (error != 0)
		{
			throw new AudioWriterOpenException(stage, $"0x{error:X4}");
		}
	}

	private void Close()
	{
		if (_handle != IntPtr.Zero)
		{
			FdkAacInterop.aacEncClose(ref _handle);
			_handle = IntPtr.Zero;
		}
	}

	private unsafe static string ReadVersion()
	{
		fixed (LibInfo* ptr = new LibInfo[39])
		{
			uint num = FdkAacInterop.aacEncGetLibInfo(ptr);
			if (num != 0)
			{
				throw new AudioWriterOpenException("encoder_load", $"0x{num:X4}");
			}
			for (int i = 0; i < 39; i++)
			{
				if (ptr[i].ModuleId == 4)
				{
					return "fdk-aac/" + ReadAscii(ptr[i].VersionStr, 32);
				}
			}
		}
		return "fdk-aac/unknown";
	}

	private unsafe static string ReadAscii(byte* text, int capacity)
	{
		int i;
		for (i = 0; i < capacity && text[i] != 0; i++)
		{
		}
		return Encoding.ASCII.GetString(text, i);
	}
}
