using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using ClientTelemetry;
using KuroWindows.RecorderCore.Clock;
using KuroWindows.RecorderCore.Encode;
using SharpGen.Runtime;
using Vortice.MediaFoundation;

namespace KuroWindows.Capture.Gen2;

internal sealed class TextureVideoEncoder : ISegmentVideoEncoder, IDisposable
{
	[StructLayout(LayoutKind.Sequential, Pack = 8)]
	private struct MfSinkWriterStats
	{
		public uint Cb;

		public long LastTimestampReceived;

		public long LastTimestampEncoded;

		public long LastTimestampProcessed;

		public long LastStreamTickReceived;

		public long LastSinkSampleRequest;

		public ulong NumSamplesReceived;

		public ulong NumSamplesEncoded;

		public ulong NumSamplesProcessed;

		public ulong NumStreamTicksReceived;

		public uint ByteCountQueued;

		public ulong ByteCountProcessed;

		public uint NumOutstandingSinkSampleRequests;

		public uint AverageSampleRateReceived;

		public uint AverageSampleRateEncoded;

		public uint AverageSampleRateProcessed;
	}

	public const string BackendName = "mf_h264_hardware_d3d11_texture";

	private const string InputMemoryPathName = "gpu_nv12_texture_sample";

	private readonly GpuEncoderContext _gpu;

	private readonly IMFSinkWriter _writer;

	private readonly string _outputPath;

	private readonly string _framesPath;

	private readonly int _displayIndex;

	private readonly int _framesPerSecond;

	private readonly uint _targetBitrate;

	private readonly int _streamIndex;

	private long _frameIndex;

	private long _lastPresentationTimeHns = -1L;

	private long _lastSlowWriteLogTicks;

	private bool _framesSidecarFailLogged;

	private long _framesSidecarFailCount;

	private FileStream? _framesStream;

	private bool _disposed;

	private Exception? _finalizeError;

	public Exception? FinalizeError => _finalizeError;

	public uint TargetBitrate => _targetBitrate;

	public string EncoderBackend => "mf_h264_hardware_d3d11_texture";

	public string InputMemoryPath => "gpu_nv12_texture_sample";

	public bool HardwareTransformsRequested => true;

	public string EncoderSelectionReason => "gen2_mft=" + _gpu.HardwareMftName;

	public string WireProfile => "legacy";

	public TextureVideoEncoder(GpuEncoderContext gpu, string outputPath, int displayIndex, int width, int height, int framesPerSecond)
	{
		if (width != gpu.Width || height != gpu.Height)
		{
			throw new ArgumentException($"Encoder size {width}x{height} does not match the Gen2 chain {gpu.Width}x{gpu.Height}.");
		}
		_gpu = gpu;
		_outputPath = outputPath;
		_framesPath = Path.ChangeExtension(outputPath, ".frames");
		_displayIndex = displayIndex;
		_framesPerSecond = Math.Max(framesPerSecond, 1);
		_targetBitrate = VideoEncoder.EstimateBitrate(width, height, _framesPerSecond);
		MediaFoundationRuntime.Acquire();
		try
		{
			Directory.CreateDirectory(Path.GetDirectoryName(outputPath));
			if (File.Exists(outputPath))
			{
				File.Delete(outputPath);
			}
			(_writer, _streamIndex) = OpenSinkWriter(gpu, outputPath, displayIndex, width, height, _framesPerSecond, _targetBitrate, logDiagnostics: true);
			KuroLog.Capture($"encoder opened path={"mf_h264_hardware_d3d11_texture"} input={"gpu_nv12_texture_sample"} hardware_transforms_requested=true reason=gen2_mft={gpu.HardwareMftName} output={Path.GetFileName(outputPath)} size={width}x{height} fps={_framesPerSecond} bitrate={_targetBitrate} input_orientation=top_down_nv12_texture", ".ctor", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\Gen2\\TextureVideoEncoder.cs", 85);
			gpu.AddUser();
		}
		catch
		{
			MediaFoundationRuntime.Release();
			throw;
		}
	}

	internal static bool TryProbeSinkWriter(GpuEncoderContext gpu, int displayIndex, int width, int height, int framesPerSecond, out string failureReason)
	{
		string text = Path.Combine(Path.GetTempPath(), $"kuro-gen2-probe-{Guid.NewGuid():N}.mp4");
		MediaFoundationRuntime.Acquire();
		IMFSinkWriter iMFSinkWriter = null;
		try
		{
			Directory.CreateDirectory(Path.GetDirectoryName(text));
			iMFSinkWriter = OpenSinkWriter(gpu, text, displayIndex, width, height, Math.Max(framesPerSecond, 1), VideoEncoder.EstimateBitrate(width, height, Math.Max(framesPerSecond, 1)), logDiagnostics: false).Writer;
			failureReason = "";
			return true;
		}
		catch (Exception ex)
		{
			failureReason = $"texture_sink_writer_open_failed_{ex.GetType().Name}_0x{ex.HResult:x8}";
			return false;
		}
		finally
		{
			try
			{
				iMFSinkWriter?.Dispose();
			}
			catch
			{
			}
			try
			{
				if (File.Exists(text))
				{
					File.Delete(text);
				}
			}
			catch
			{
			}
			MediaFoundationRuntime.Release();
		}
	}

	private static (IMFSinkWriter Writer, int StreamIndex) OpenSinkWriter(GpuEncoderContext gpu, string outputPath, int displayIndex, int width, int height, int framesPerSecond, uint targetBitrate, bool logDiagnostics)
	{
		using IMFAttributes attributes = gpu.CreateSinkWriterAttributes();
		IMFSinkWriter iMFSinkWriter = MediaFactory.MFCreateSinkWriterFromURL(outputPath, null, attributes);
		try
		{
			using IMFMediaType targetMediaType = H264EncoderConfig.CreateOutputMediaType(width, height, framesPerSecond, targetBitrate, H264WireProfile.Legacy);
			int num = iMFSinkWriter.AddStream(targetMediaType);
			using IMFMediaType iMFMediaType = MediaFactory.MFCreateMediaType();
			iMFMediaType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
			iMFMediaType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.NV12);
			iMFMediaType.Set(MediaTypeAttributeKeys.InterlaceMode, 2u);
			iMFMediaType.Set(MediaTypeAttributeKeys.FrameSize, Pack(width, height));
			iMFMediaType.Set(MediaTypeAttributeKeys.FrameRate, Pack(framesPerSecond, 1));
			iMFMediaType.Set(MediaTypeAttributeKeys.PixelAspectRatio, Pack(1, 1));
			iMFSinkWriter.SetInputMediaType(num, iMFMediaType, null);
			H264EncoderProperties properties = H264EncoderConfig.ConfigureEncoderProperties(iMFSinkWriter, num, targetBitrate, banBFrames: true, enableCabac: false);
			if (logDiagnostics)
			{
				LogEncoderProperties(displayIndex, targetBitrate, in properties);
			}
			AssertNoVideoProcessorInChain(iMFSinkWriter, num, displayIndex, logDiagnostics);
			if (logDiagnostics)
			{
				H264EncoderChainInfo h264EncoderChainInfo = H264EncoderConfig.TryReadEncoderInfo(iMFSinkWriter, num);
				KuroLog.Capture($"gen2 encoder chain negotiated display={displayIndex} mft=\"{h264EncoderChainInfo.EncoderMftName ?? "-"}\" hw_mft={(h264EncoderChainInfo.IsHardwareMft ? "true" : "false")} negotiated_profile={h264EncoderChainInfo.NegotiatedProfile?.ToString() ?? "-"} negotiated_level={h264EncoderChainInfo.NegotiatedLevel?.ToString() ?? "-"}", "OpenSinkWriter", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\Gen2\\TextureVideoEncoder.cs", 193);
			}
			iMFSinkWriter.BeginWriting();
			return (Writer: iMFSinkWriter, StreamIndex: num);
		}
		catch
		{
			iMFSinkWriter.Dispose();
			throw;
		}
	}

	public EncodedFrameCommit WriteFrame(in FrameCommitRequest frame, long presentationTimeHns, long durationHns, long committedWallMs, TimestampQuality timestampQuality)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		if (frame.GpuSampleSlot < 0)
		{
			throw new ArgumentException("gpu_v2 commit without a GPU sample slot — the Gen2 backend must convert before committing.", "frame");
		}
		if (_lastPresentationTimeHns >= 0 && presentationTimeHns <= _lastPresentationTimeHns)
		{
			presentationTimeHns = _lastPresentationTimeHns + 1;
		}
		durationHns = Math.Max(1L, durationHns);
		_lastPresentationTimeHns = presentationTimeHns;
		long timestamp = Stopwatch.GetTimestamp();
		_gpu.WriteSampleToWriter(_writer, _streamIndex, frame.GpuSampleSlot, presentationTimeHns, durationHns);
		long num = ElapsedMs(timestamp);
		LogSlowWriteIfNeeded(num);
		EncodedFrameCommit result = new EncodedFrameCommit(_frameIndex, presentationTimeHns * 100, durationHns * 100, committedWallMs, frame.FrameKind, timestampQuality, frame.TimestampKind, frame.Backend, num);
		_frameIndex++;
		PersistFrameCount();
		return result;
	}

	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}
		bool flag = false;
		try
		{
			PersistFrameCount();
			if (_framesSidecarFailCount > 0)
			{
				KuroLog.Capture($"frames sidecar suppressed_writes={_framesSidecarFailCount} {Path.GetFileName(_outputPath)}", "Dispose", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\Gen2\\TextureVideoEncoder.cs", 277);
			}
			if (_frameIndex <= 0)
			{
				KuroLog.Capture("finalize skipped " + Path.GetFileName(_outputPath) + " frames=0", "Dispose", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\Gen2\\TextureVideoEncoder.cs", 281);
				return;
			}
			Exception error;
			switch (BoundedFinalize.Drain(_writer, _streamIndex, TimeSpan.FromSeconds(20.0), out error))
			{
			case BoundedFinalizeOutcome.Ok:
				LogSinkStatistics();
				KuroLog.Capture($"finalized {Path.GetFileName(_outputPath)} frames={_frameIndex}", "Dispose", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\Gen2\\TextureVideoEncoder.cs", 296);
				break;
			case BoundedFinalizeOutcome.Wedged:
				flag = true;
				LeakedWriterCounter.Increment();
				_finalizeError = new TimeoutException($"sink writer finalize exceeded {20}s (encoder wedge); writer leaked.");
				KuroLog.Capture($"finalize TIMED OUT {Path.GetFileName(_outputPath)} frames={_frameIndex} — leaking wedged writer/chain refs", "Dispose", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\Gen2\\TextureVideoEncoder.cs", 309);
				break;
			default:
				_finalizeError = error;
				KuroLog.Capture($"finalize failed {Path.GetFileName(_outputPath)} frames={_frameIndex}: {error}", "Dispose", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\Gen2\\TextureVideoEncoder.cs", 317);
				break;
			}
		}
		finally
		{
			try
			{
				_framesStream?.Dispose();
			}
			catch
			{
			}
			_framesStream = null;
			_disposed = true;
			if (!flag)
			{
				_writer.Dispose();
				MediaFoundationRuntime.Release();
				_gpu.ReleaseUser();
			}
		}
	}

	private void LogSinkStatistics()
	{
		try
		{
			MfSinkWriterStats sinkWriterStatistics = GetSinkWriterStatistics(_writer, _streamIndex);
			KuroLog.Capture($"sink stats display={_displayIndex} received={sinkWriterStatistics.NumSamplesReceived} processed={sinkWriterStatistics.NumSamplesProcessed} committed={_frameIndex} encoded={sinkWriterStatistics.NumSamplesEncoded} bytes={sinkWriterStatistics.ByteCountProcessed} output={_outputPath}", "LogSinkStatistics", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\Gen2\\TextureVideoEncoder.cs", 346);
			if (sinkWriterStatistics.NumSamplesProcessed != (ulong)_frameIndex)
			{
				KuroLog.Capture($"sink stats MISMATCH display={_displayIndex} processed={sinkWriterStatistics.NumSamplesProcessed} committed={_frameIndex} output={Path.GetFileName(_outputPath)}", "LogSinkStatistics", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\Gen2\\TextureVideoEncoder.cs", 352);
			}
		}
		catch (Exception ex)
		{
			KuroLog.Capture($"sink stats unavailable display={_displayIndex} output={Path.GetFileName(_outputPath)}: {ex.Message}", "LogSinkStatistics", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\Gen2\\TextureVideoEncoder.cs", 359);
		}
	}

	private static void AssertNoVideoProcessorInChain(IMFSinkWriter writer, int streamIndex, int displayIndex, bool logDiagnostics)
	{
		Guid guidKey = new Guid(738916889, 31383, 19802, 158, 232, 22, 212, 252, 81, 141, 140);
		try
		{
			using IMFSinkWriterEx iMFSinkWriterEx = writer.QueryInterface<IMFSinkWriterEx>();
			int num = 0;
			int num2 = 0;
			while (true)
			{
				Guid guidCategory;
				IMFTransform transform;
				try
				{
					iMFSinkWriterEx.GetTransformForStream(streamIndex, num2, out guidCategory, out transform);
				}
				catch (SharpGenException)
				{
					break;
				}
				using (transform)
				{
					num++;
					transform.Attributes?.Set(guidKey, 1u);
					if (logDiagnostics)
					{
						KuroLog.Capture($"gen2 encoder chain display={displayIndex} transform_index={num2} category={guidCategory}", "AssertNoVideoProcessorInChain", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\Gen2\\TextureVideoEncoder.cs", 400);
					}
				}
				num2++;
			}
			if (logDiagnostics)
			{
				KuroLog.Capture($"gen2 encoder chain display={displayIndex} transform_count={num}", "AssertNoVideoProcessorInChain", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\Gen2\\TextureVideoEncoder.cs", 408);
			}
		}
		catch (Exception ex2)
		{
			if (logDiagnostics)
			{
				KuroLog.Capture($"gen2 encoder chain walk failed display={displayIndex}: {ex2.Message}", "AssertNoVideoProcessorInChain", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\Gen2\\TextureVideoEncoder.cs", 415);
			}
		}
	}

	private static void LogEncoderProperties(int displayIndex, uint targetBitrate, in H264EncoderProperties properties)
	{
		if (properties.Error != null && !properties.RcModeHr.HasValue)
		{
			KuroLog.Capture($"gen2 rate-control unavailable display={displayIndex}: {properties.Error}", "LogEncoderProperties", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\Gen2\\TextureVideoEncoder.cs", 444);
			return;
		}
		KuroLog.Capture($"gen2 rate-control display={displayIndex} rc_hr={H264EncoderConfig.FormatHr(properties.RcModeHr)} mean_hr={H264EncoderConfig.FormatHr(properties.MeanBitRateHr)} bframes_hr={H264EncoderConfig.FormatHr(properties.BFramesHr)} bframes_ban={(properties.BFramesBanConfirmed ? "confirmed" : "unconfirmed")} mean_bps={targetBitrate} {(properties.RateControlOk ? "ok" : "unsupported_by_mft")}", "LogEncoderProperties", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\Gen2\\TextureVideoEncoder.cs", 448);
	}

	private static long ElapsedMs(long startTicks)
	{
		return Math.Max(0L, (long)Math.Round((double)(Stopwatch.GetTimestamp() - startTicks) * 1000.0 / (double)Stopwatch.Frequency));
	}

	private void LogSlowWriteIfNeeded(long elapsedMs)
	{
		if (elapsedMs >= 250)
		{
			long timestamp = Stopwatch.GetTimestamp();
			long num = Interlocked.Read(in _lastSlowWriteLogTicks);
			if ((num == 0L || timestamp - num >= Stopwatch.Frequency * 2) && Interlocked.CompareExchange(ref _lastSlowWriteLogTicks, timestamp, num) == num)
			{
				KuroLog.Capture($"encoder write throttled {Path.GetFileName(_outputPath)} frame={_frameIndex} elapsed_ms={elapsedMs}", "LogSlowWriteIfNeeded", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\Gen2\\TextureVideoEncoder.cs", 477);
				Count.EncoderThrottleEvents(1L);
			}
		}
	}

	private void PersistFrameCount()
	{
		try
		{
			if (_framesStream == null)
			{
				_framesStream = new FileStream(_framesPath, FileMode.Create, FileAccess.Write, FileShare.Read);
			}
			byte[] bytes = Encoding.ASCII.GetBytes(_frameIndex.ToString());
			_framesStream.Seek(0L, SeekOrigin.Begin);
			_framesStream.Write(bytes, 0, bytes.Length);
			_framesStream.SetLength(bytes.Length);
			_framesStream.Flush();
			if (_framesSidecarFailLogged)
			{
				_framesSidecarFailLogged = false;
				KuroLog.Capture($"frames sidecar write recovered {Path.GetFileName(_outputPath)} suppressed_count={_framesSidecarFailCount}", "PersistFrameCount", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\Gen2\\TextureVideoEncoder.cs", 500);
				_framesSidecarFailCount = 0L;
			}
		}
		catch (Exception ex)
		{
			try
			{
				_framesStream?.Dispose();
			}
			catch
			{
			}
			_framesStream = null;
			_framesSidecarFailCount++;
			if (!_framesSidecarFailLogged)
			{
				_framesSidecarFailLogged = true;
				KuroLog.Capture("frames sidecar write failed " + Path.GetFileName(_outputPath) + ": " + ex.Message, "PersistFrameCount", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\Gen2\\TextureVideoEncoder.cs", 516);
			}
		}
	}

	private static ulong Pack(int high, int low)
	{
		return ((ulong)(uint)high << 32) | (uint)low;
	}

	private unsafe static MfSinkWriterStats GetSinkWriterStatistics(IMFSinkWriter writer, int streamIndex)
	{
		MfSinkWriterStats result = new MfSinkWriterStats
		{
			Cb = (uint)sizeof(MfSinkWriterStats)
		};
		int num = ((delegate* unmanaged[Stdcall]<nint, int, MfSinkWriterStats*, int>)(*(IntPtr*)((nint)(*(IntPtr*)writer.NativePointer) + (nint)13 * (nint)sizeof(void*))))(writer.NativePointer, streamIndex, &result);
		if (num < 0)
		{
			throw new InvalidOperationException($"GetStatistics vtable call failed hr=0x{num:x8}");
		}
		return result;
	}

	EncodedFrameCommit ISegmentVideoEncoder.WriteFrame(in FrameCommitRequest frame, long presentationTimeHns, long durationHns, long committedWallMs, TimestampQuality timestampQuality)
	{
		return WriteFrame(in frame, presentationTimeHns, durationHns, committedWallMs, timestampQuality);
	}
}
