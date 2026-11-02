using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using ClientTelemetry;
using KuroWindows.RecorderCore.Capture;
using KuroWindows.RecorderCore.Clock;
using KuroWindows.RecorderCore.Encode;
using SharpGen.Runtime;
using Vortice.MediaFoundation;

namespace KuroWindows.Capture;

public sealed class VideoEncoder : IDisposable, ISegmentVideoEncoder
{
	private sealed class WireProfileRejectedException : Exception
	{
		public WireProfileRejectedException(string message)
			: base(message)
		{
			HResult = 0;
		}
	}

	private const string InputMemoryPathName = "cpu_bgra_memory_sample";

	internal const string BackendName = "mf_h264_software_cpu_memory";

	private readonly IMFSinkWriter _writer;

	private readonly string _outputPath;

	private readonly string _framesPath;

	private readonly string _selectionReason;

	private readonly string _wireProfile;

	private readonly int _streamIndex;

	private readonly int _frameSizeBytes;

	private readonly int _framesPerSecond;

	private readonly int _width;

	private readonly int _height;

	private readonly int _stride;

	private readonly uint _targetBitrate;

	private long _frameIndex;

	private bool _disposed;

	private long _lastPresentationTimeHns = -1L;

	private long _lastSlowWriteLogTicks;

	private bool _framesSidecarWriteFailLogged;

	private FileStream? _framesStream;

	private Exception? _finalizeError;

	public Exception? FinalizeError => _finalizeError;

	public uint TargetBitrate => _targetBitrate;

	public string EncoderBackend => "mf_h264_software_cpu_memory";

	public string InputMemoryPath => "cpu_bgra_memory_sample";

	public bool HardwareTransformsRequested => false;

	public string EncoderSelectionReason => _selectionReason;

	public string WireProfile => _wireProfile;

	public VideoEncoder(string outputPath, int width, int height, int framesPerSecond = 30)
	{
		_outputPath = outputPath;
		_framesPath = Path.ChangeExtension(_outputPath, ".frames");
		checked
		{
			_frameSizeBytes = width * height * 4;
			_width = width;
			_height = height;
			_stride = width * 4;
			_framesPerSecond = Math.Max(framesPerSecond, 1);
			_targetBitrate = EstimateBitrate(width, height, _framesPerSecond);
			MediaFoundationRuntime.Acquire();
			try
			{
				OpenedWriter openedWriter = OpenWriter(outputPath, width, height);
				_writer = openedWriter.Writer;
				_streamIndex = openedWriter.StreamIndex;
				_wireProfile = openedWriter.WireProfile;
				_selectionReason = "software_locked;wire_profile=" + openedWriter.WireProfile;
				Log($"encoder opened path={"mf_h264_software_cpu_memory"} input={"cpu_bgra_memory_sample"} wire_profile={openedWriter.WireProfile} wire_profile_source={openedWriter.WireProfileSource} mft=\"{openedWriter.Chain.EncoderMftName ?? "-"}\" hw_mft={(openedWriter.Chain.IsHardwareMft ? "true" : "false")} negotiated_profile={openedWriter.Chain.NegotiatedProfile?.ToString() ?? "-"} negotiated_level={openedWriter.Chain.NegotiatedLevel?.ToString() ?? "-"} output={Path.GetFileName(outputPath)} size={width}x{height} fps={_framesPerSecond} bitrate={_targetBitrate} input_stride_bytes={_stride} " + "input_orientation=bottom_up_rgb_memory", ".ctor", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\VideoEncoder.cs", 67);
			}
			catch
			{
				MediaFoundationRuntime.Release();
				throw;
			}
		}
	}

	EncodedFrameCommit ISegmentVideoEncoder.WriteFrame(in FrameCommitRequest frame, long presentationTimeHns, long durationHns, long committedWallMs, TimestampQuality timestampQuality)
	{
		return WriteFrame(frame.BgraData, presentationTimeHns, durationHns, committedWallMs, timestampQuality, frame.TimestampKind, frame.FrameKind, frame.Backend);
	}

	public EncodedFrameCommit WriteFrame(byte[] bgraData, long presentationTimeHns, long durationHns, long wallMs, TimestampQuality timestampQuality = TimestampQuality.Authoritative, TimestampKind timestampKind = TimestampKind.AcquisitionQpc, CaptureFrameKind frameKind = CaptureFrameKind.Source, CaptureBackendKind backend = CaptureBackendKind.DesktopDuplication)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		if (bgraData.Length != _frameSizeBytes)
		{
			throw new ArgumentException($"Expected {_frameSizeBytes} bytes but received {bgraData.Length}.", "bgraData");
		}
		if (_lastPresentationTimeHns >= 0 && presentationTimeHns <= _lastPresentationTimeHns)
		{
			presentationTimeHns = _lastPresentationTimeHns + 1;
		}
		durationHns = Math.Max(1L, durationHns);
		_lastPresentationTimeHns = presentationTimeHns;
		using IMFMediaBuffer iMFMediaBuffer = MediaFactory.MFCreateMemoryBuffer(_frameSizeBytes);
		nint ppbBuffer = IntPtr.Zero;
		iMFMediaBuffer.Lock(out ppbBuffer, out var _, out var _);
		try
		{
			CopyTopDownBgraToBottomUpBuffer(bgraData, ppbBuffer, _width, _height, _stride);
			iMFMediaBuffer.CurrentLength = bgraData.Length;
		}
		finally
		{
			if (ppbBuffer != IntPtr.Zero)
			{
				iMFMediaBuffer.Unlock();
			}
		}
		using IMFSample iMFSample = MediaFactory.MFCreateSample();
		iMFSample.AddBuffer(iMFMediaBuffer);
		iMFSample.SampleTime = presentationTimeHns;
		iMFSample.SampleDuration = durationHns;
		long timestamp = Stopwatch.GetTimestamp();
		_writer.WriteSample(_streamIndex, iMFSample);
		long num = ElapsedMs(timestamp);
		LogSlowWriteIfNeeded(num);
		EncodedFrameCommit result = new EncodedFrameCommit(_frameIndex, presentationTimeHns * 100, durationHns * 100, wallMs, frameKind, timestampQuality, timestampKind, backend, num);
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
			if (_frameIndex <= 0)
			{
				Log("finalize skipped " + Path.GetFileName(_outputPath) + " frames=0", "Dispose", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\VideoEncoder.cs", 197);
				return;
			}
			Exception error;
			switch (BoundedFinalize.Drain(_writer, _streamIndex, TimeSpan.FromSeconds(20.0), out error))
			{
			case BoundedFinalizeOutcome.Ok:
				Log($"finalized {Path.GetFileName(_outputPath)} frames={_frameIndex}", "Dispose", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\VideoEncoder.cs", 208);
				break;
			case BoundedFinalizeOutcome.Wedged:
				flag = true;
				LeakedWriterCounter.Increment();
				_finalizeError = new TimeoutException($"sink writer finalize exceeded {20}s (encoder wedge); writer leaked.");
				Log($"finalize TIMED OUT {Path.GetFileName(_outputPath)} frames={_frameIndex} — leaking wedged writer", "Dispose", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\VideoEncoder.cs", 222);
				break;
			default:
				_finalizeError = error;
				Log($"finalize failed {Path.GetFileName(_outputPath)} frames={_frameIndex}: {error}", "Dispose", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\VideoEncoder.cs", 229);
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
			}
		}
	}

	private static void Log(string message, [CallerMemberName] string memberName = "", [CallerFilePath] string filePath = "", [CallerLineNumber] int lineNumber = 0)
	{
		KuroLog.Capture(message, memberName, filePath, lineNumber);
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
				Log($"encoder write throttled {Path.GetFileName(_outputPath)} frame={_frameIndex} elapsed_ms={elapsedMs}", "LogSlowWriteIfNeeded", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\VideoEncoder.cs", 273);
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
			_framesSidecarWriteFailLogged = false;
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
			if (!_framesSidecarWriteFailLogged)
			{
				_framesSidecarWriteFailLogged = true;
				Log("frames sidecar write failed " + Path.GetFileName(_outputPath) + ": " + ex.Message, "PersistFrameCount", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\VideoEncoder.cs", 307);
			}
		}
	}

	private IMFMediaType CreateInputMediaType(int width, int height)
	{
		IMFMediaType iMFMediaType = MediaFactory.MFCreateMediaType();
		iMFMediaType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
		iMFMediaType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.Argb32);
		iMFMediaType.Set(MediaTypeAttributeKeys.InterlaceMode, 2u);
		iMFMediaType.Set(MediaTypeAttributeKeys.FrameSize, PackToUint64(width, height));
		iMFMediaType.Set(MediaTypeAttributeKeys.FrameRate, PackToUint64(_framesPerSecond, 1));
		iMFMediaType.Set(MediaTypeAttributeKeys.PixelAspectRatio, PackToUint64(1, 1));
		return iMFMediaType;
	}

	private unsafe static void CopyTopDownBgraToBottomUpBuffer(byte[] source, nint destination, int width, int height, int stride)
	{
		fixed (byte* ptr = source)
		{
			for (int i = 0; i < height; i++)
			{
				byte* source2 = ptr + i * stride;
				byte* destination2 = (byte*)(destination + (height - 1 - i) * stride);
				Buffer.MemoryCopy(source2, destination2, stride, checked(width * 4));
			}
		}
	}

	internal static uint EstimateBitrate(int width, int height, int framesPerSecond)
	{
		long num = (long)width * (long)height;
		VideoBitrateSnapshot videoBitrateSnapshot = VideoBitratePolicy.Snapshot();
		if ((object)videoBitrateSnapshot != null)
		{
			return (uint)Math.Clamp((long)Math.Round((double)num * videoBitrateSnapshot.BitsPerPixelPerSecond), (long)Math.Round(videoBitrateSnapshot.MinMbps * 1000000.0), (long)Math.Round(videoBitrateSnapshot.MaxMbps * 1000000.0));
		}
		return (uint)Math.Clamp(num * Math.Max(framesPerSecond, 1) * 5, 10000000L, 16000000L);
	}

	private static ulong PackToUint64(int high, int low)
	{
		return ((ulong)(uint)high << 32) | (uint)low;
	}

	private static void LogEncoderProperties(string outputPath, uint targetBitrate, in H264EncoderProperties properties, H264WireProfile profile, (uint? BFrameCount, bool? CabacEnabled) readbackPre, (uint? BFrameCount, bool? CabacEnabled) readback)
	{
		if (properties.Error != null && !properties.RcModeHr.HasValue)
		{
			Log("cpu_v1 rate-control unavailable output=" + Path.GetFileName(outputPath) + ": " + properties.Error, "LogEncoderProperties", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\VideoEncoder.cs", 394);
			return;
		}
		string text;
		if (profile != H264WireProfile.High)
		{
			text = "";
		}
		else
		{
			DefaultInterpolatedStringHandler defaultInterpolatedStringHandler = new DefaultInterpolatedStringHandler(38, 3);
			defaultInterpolatedStringHandler.AppendLiteral("bframes_rb_pre=");
			defaultInterpolatedStringHandler.AppendFormatted(readbackPre.BFrameCount?.ToString() ?? "?");
			defaultInterpolatedStringHandler.AppendLiteral(" ");
			defaultInterpolatedStringHandler.AppendLiteral("bframes_rb=");
			defaultInterpolatedStringHandler.AppendFormatted(readback.BFrameCount?.ToString() ?? "?");
			defaultInterpolatedStringHandler.AppendLiteral(" ");
			defaultInterpolatedStringHandler.AppendLiteral("cabac_rb=");
			bool? item = readback.CabacEnabled;
			string value;
			if (item.HasValue)
			{
				value = ((item == true) ? "on" : "off");
			}
			else
			{
				value = "?";
			}
			defaultInterpolatedStringHandler.AppendFormatted(value);
			defaultInterpolatedStringHandler.AppendLiteral(" ");
			text = defaultInterpolatedStringHandler.ToStringAndClear();
		}
		string text2 = text;
		Log($"cpu_v1 rate-control output={Path.GetFileName(outputPath)} rc_hr={H264EncoderConfig.FormatHr(properties.RcModeHr)} mean_hr={H264EncoderConfig.FormatHr(properties.MeanBitRateHr)} bframes_hr={H264EncoderConfig.FormatHr(properties.BFramesHr)} cabac_hr={H264EncoderConfig.FormatHr(properties.CabacHr)} " + text2 + $"mean_bps={targetBitrate} {(properties.RateControlOk ? "ok" : "unsupported_by_mft")}", "LogEncoderProperties", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\VideoEncoder.cs", 408);
	}

	private OpenedWriter OpenWriter(string outputPath, int width, int height)
	{
		Directory.CreateDirectory(Path.GetDirectoryName(outputPath));
		DeletePartialOutputIfPresent(outputPath);
		(string Profile, string Source) tuple = H264WireProfilePolicy.SnapshotForOpen();
		string item = tuple.Profile;
		string item2 = tuple.Source;
		bool highRequested = string.Equals(item, "high", StringComparison.Ordinal) && H264WireProfilePolicy.HighAttemptWorthwhile;
		List<Exception> list = new List<Exception>();
		List<string> list2 = new List<string>();
		foreach (H264WireProfile item3 in WireProfileAttempts(highRequested))
		{
			string text = ((item3 == H264WireProfile.High) ? "high" : "legacy");
			string value = "create_sink_writer";
			IMFSinkWriter iMFSinkWriter = null;
			try
			{
				iMFSinkWriter = CreateSinkWriter(outputPath, item3);
				using IMFMediaType targetMediaType = H264EncoderConfig.CreateOutputMediaType(width, height, _framesPerSecond, _targetBitrate, item3);
				value = "add_stream";
				int streamIndex = iMFSinkWriter.AddStream(targetMediaType);
				using IMFMediaType inputMediaType = CreateInputMediaType(width, height);
				value = "set_input_media_type";
				if (item3 == H264WireProfile.High)
				{
					using IMFAttributes encodingParameters = H264EncoderConfig.CreateHighProfileEncodingParameters();
					iMFSinkWriter.SetInputMediaType(streamIndex, inputMediaType, encodingParameters);
				}
				else
				{
					iMFSinkWriter.SetInputMediaType(streamIndex, inputMediaType, null);
				}
				value = "configure_transform_chain";
				string text2 = H264EncoderConfig.DisableFrameRateConversion(iMFSinkWriter, streamIndex);
				if (text2 != null)
				{
					Log("xvp frc disable failed output=" + Path.GetFileName(outputPath) + ": " + text2, "OpenWriter", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\VideoEncoder.cs", 474);
				}
				H264EncoderChainInfo chain = H264EncoderConfig.TryReadEncoderInfo(iMFSinkWriter, streamIndex);
				value = "configure_encoder";
				(uint?, bool?) tuple2 = ((item3 == H264WireProfile.High) ? H264EncoderConfig.TryReadBackEncoderProperties(iMFSinkWriter, streamIndex) : (null, null));
				H264EncoderProperties properties = H264EncoderConfig.ConfigureEncoderProperties(iMFSinkWriter, streamIndex, _targetBitrate, item3 == H264WireProfile.High, item3 == H264WireProfile.High);
				value = "verify_encoder";
				(uint?, bool?) readback = ((item3 == H264WireProfile.High) ? H264EncoderConfig.TryReadBackEncoderProperties(iMFSinkWriter, streamIndex) : (null, null));
				LogEncoderProperties(outputPath, _targetBitrate, in properties, item3, tuple2, readback);
				if (item3 == H264WireProfile.High)
				{
					var (num, _) = tuple2;
					if (!num.HasValue || num.GetValueOrDefault() != 0)
					{
						throw new WireProfileRejectedException("bframes_ban_unconfirmed_pre_negotiation bframes_rb_pre=" + (tuple2.Item1?.ToString() ?? "-") + " bframes_rb=" + (readback.Item1?.ToString() ?? "-") + " bframes_hr=" + H264EncoderConfig.FormatHr(properties.BFramesHr));
					}
				}
				value = "begin_writing";
				iMFSinkWriter.BeginWriting();
				if (item3 == H264WireProfile.High)
				{
					H264WireProfilePolicy.RecordHighOpenOutcome(succeeded: true);
				}
				if (list2.Count > 0)
				{
					Log($"encoder sink writer opened after profile fallback selected_profile={text} failed_profiles={string.Join(",", list2)} output={Path.GetFileName(outputPath)} size={width}x{height} fps={_framesPerSecond}", "OpenWriter", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\VideoEncoder.cs", 525);
				}
				return new OpenedWriter(iMFSinkWriter, streamIndex, text, item2, chain);
			}
			catch (Exception ex)
			{
				try
				{
					iMFSinkWriter?.Dispose();
				}
				catch
				{
				}
				list.Add(ex);
				list2.Add($"{text}:{value}:{DescribeHResult(ex)}");
				DeletePartialOutputIfPresent(outputPath);
				if (item3 == H264WireProfile.High)
				{
					H264WireProfilePolicy.RecordHighOpenOutcome(succeeded: false);
				}
			}
		}
		Log($"encoder sink writer open failed all profiles failed_profiles={string.Join(",", list2)} output={Path.GetFileName(outputPath)} size={width}x{height} fps={_framesPerSecond}", "OpenWriter", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\VideoEncoder.cs", 555);
		throw new AggregateException("Media Foundation H.264 sink writer failed for all wire profiles.", list);
	}

	private static IEnumerable<H264WireProfile> WireProfileAttempts(bool highRequested)
	{
		if (highRequested)
		{
			yield return H264WireProfile.High;
		}
		yield return H264WireProfile.Legacy;
	}

	private static IMFSinkWriter CreateSinkWriter(string outputPath, H264WireProfile profile)
	{
		if (profile == H264WireProfile.Legacy)
		{
			return MediaFactory.MFCreateSinkWriterFromURL(outputPath, null, null);
		}
		using IMFAttributes iMFAttributes = MediaFactory.MFCreateAttributes(1u);
		nint num = H264EncoderConfig.CreateHighProfileEncoderConfigStore(out string error);
		if (num == IntPtr.Zero)
		{
			Log("encoder config store unavailable output=" + Path.GetFileName(outputPath) + ": " + error, "CreateSinkWriter", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\VideoEncoder.cs", 624);
		}
		else
		{
			using ComObject unknown = new ComObject(num);
			iMFAttributes.Set(SinkWriterAttributeKeys.EncoderConfig, unknown);
		}
		return MediaFactory.MFCreateSinkWriterFromURL(outputPath, null, iMFAttributes);
	}

	private void DeletePartialOutputIfPresent(string outputPath)
	{
		try
		{
			if (File.Exists(outputPath))
			{
				File.Delete(outputPath);
			}
		}
		catch (Exception ex)
		{
			Log("partial encoder output cleanup failed output=" + Path.GetFileName(outputPath) + ": " + ex.Message, "DeletePartialOutputIfPresent", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\VideoEncoder.cs", 646);
		}
	}

	private static string DescribeHResult(Exception ex)
	{
		int hResult = ex.HResult;
		if (hResult != 0)
		{
			return $"0x{hResult:X8}";
		}
		return ex.GetType().Name.ToLowerInvariant();
	}
}
