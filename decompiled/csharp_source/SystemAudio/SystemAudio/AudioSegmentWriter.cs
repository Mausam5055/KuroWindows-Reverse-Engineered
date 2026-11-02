using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Vortice.MediaFoundation;

namespace SystemAudio;

internal sealed class AudioSegmentWriter
{
	private sealed record Counters(long ZerosFilled, long SamplesDropped, long Corrections, long TimestampErrors, long Discontinuities, long DeviceReopens, long DroppedMs, long StarvedPeriods)
	{
		public static Counters Read(SampleRing ring, CaptureCounters capture)
		{
			return new Counters(ring.ZerosFilled, ring.SamplesDropped, ring.Corrections, ring.TimestampErrors, ring.Discontinuities, capture.DeviceReopens, ring.DroppedMs, capture.StarvedPeriods);
		}

		public Counters Since(Counters opened)
		{
			return new Counters(ZerosFilled - opened.ZerosFilled, SamplesDropped - opened.SamplesDropped, Corrections - opened.Corrections, TimestampErrors - opened.TimestampErrors, Discontinuities - opened.Discontinuities, DeviceReopens - opened.DeviceReopens, DroppedMs - opened.DroppedMs, StarvedPeriods - opened.StarvedPeriods);
		}
	}

	public static readonly TimeSpan FinalizeTimeout = TimeSpan.FromSeconds(20.0);

	private const int FrameLength = 1024;

	private const int SamplesPerBlock = 2048;

	private const int SoundThreshold = 32;

	private readonly string _path;

	private readonly string _fileName;

	private readonly int _segmentIndex;

	private readonly long _anchorWallMs;

	private readonly long _anchorQpc;

	private readonly long _anchorIndex;

	private readonly SampleRing _ring;

	private readonly CaptureCounters _captureCounters;

	private readonly Func<(SystemAudioMixFormat? MixFormat, bool Keepalive)> _streamFacts;

	private readonly short[] _pcm = new short[2048];

	private readonly byte[] _accessUnit;

	private readonly int _delay;

	private readonly string _encoderVersion;

	private readonly IMFSinkWriter _writer;

	private readonly int _streamIndex;

	private readonly Counters _opened;

	private AacEncoder? _encoder;

	private long _cursor;

	private long _frames;

	private long _zerosRead;

	private long _maxAbsErrorUs;

	private bool _maxErrorFrozen;

	private long _sealTailZeros;

	private long _sealOverrun;

	private long _soundSeconds;

	private long _lastSoundSecond = -1L;

	private volatile bool _overrunKnown;

	private readonly long _producedAtOpen;

	private long _end = long.MaxValue;

	private long _endWallMs = -1L;

	private long _interruptedAtMs;

	private volatile string? _interruptedReason;

	private volatile string? _finalizedPath;

	private volatile Counters? _sealed;

	private volatile bool _stopFeeding;

	private volatile bool _sealing;

	public int SegmentIndex => _segmentIndex;

	public long End => Volatile.Read(in _end);

	public bool IsSealing => _sealing;

	public AudioSegmentWriter(string sessionDir, int segmentIndex, long anchorWallMs, long anchorQpc, SampleRing ring, AudioClock clock, CaptureCounters captureCounters, Func<(SystemAudioMixFormat? MixFormat, bool Keepalive)> streamFacts)
	{
		_segmentIndex = segmentIndex;
		_anchorWallMs = anchorWallMs;
		_anchorQpc = anchorQpc;
		_anchorIndex = clock.IndexFromQpc(anchorQpc);
		_ring = ring;
		_captureCounters = captureCounters;
		_streamFacts = streamFacts;
		_fileName = $"audio_{segmentIndex}.mp4";
		_path = Path.Combine(sessionDir, _fileName);
		_opened = Counters.Read(ring, captureCounters);
		_producedAtOpen = ring.ProducedUpTo;
		ring.ResetMaxAbsError();
		MediaFoundationLifetime.Acquire();
		string stage = "encoder_load";
		IMFSinkWriter iMFSinkWriter = null;
		try
		{
			_encoder = new AacEncoder();
			_delay = _encoder.Delay;
			_encoderVersion = _encoder.Version;
			_accessUnit = new byte[Math.Max(_encoder.MaxOutBufBytes, 1)];
			_cursor = _anchorIndex + _delay;
			stage = "create_sink_writer";
			iMFSinkWriter = MediaFactory.MFCreateSinkWriterFromURL(_path, null, null);
			stage = "add_stream";
			using IMFMediaType iMFMediaType = CreateAacType(_encoder.AudioSpecificConfig);
			_streamIndex = iMFSinkWriter.AddStream(iMFMediaType);
			stage = "set_input_media_type";
			iMFSinkWriter.SetInputMediaType(_streamIndex, iMFMediaType, null);
			stage = "begin_writing";
			iMFSinkWriter.BeginWriting();
			_writer = iMFSinkWriter;
		}
		catch (Exception ex)
		{
			_encoder?.Dispose();
			_encoder = null;
			iMFSinkWriter?.Dispose();
			DeletePartialFile();
			MediaFoundationLifetime.Release();
			if (ex is AudioWriterOpenException)
			{
				throw;
			}
			throw new AudioWriterOpenException(stage, $"0x{ex.HResult:X8}", ex);
		}
	}

	public void SetEnd(long endIndex, long endWallMs)
	{
		if (endIndex < Volatile.Read(in _end))
		{
			Volatile.Write(ref _endWallMs, endWallMs);
			Volatile.Write(ref _end, endIndex);
		}
	}

	public void Feed(long upTo)
	{
		Feed(upTo, ref _zerosRead);
	}

	private void Feed(long upTo, ref long zeros)
	{
		upTo = Math.Min(upTo, Volatile.Read(in _end));
		if (_stopFeeding)
		{
			_ring.AdvanceConsumed(upTo);
		}
		else
		{
			if (_sealing)
			{
				return;
			}
			while (_cursor + 1024 <= upTo)
			{
				Interlocked.Add(ref zeros, _ring.Read(_cursor, _pcm, 1024));
				if (!EncodeAndWrite())
				{
					return;
				}
				_cursor += 1024L;
			}
			SampleMaxError();
		}
	}

	public void FreezeMaxError()
	{
		SampleMaxError();
		_maxErrorFrozen = true;
	}

	public void FeedTo(long end, int waitMs)
	{
		long num = Environment.TickCount64 + waitMs;
		while (_ring.ProducedUpTo < end && Environment.TickCount64 < num && !_stopFeeding)
		{
			Thread.Sleep(5);
		}
		Feed(end, ref _sealTailZeros);
		if (_stopFeeding || _sealing)
		{
			return;
		}
		long num2 = end - _cursor;
		if (num2 > 0 && num2 < 1024)
		{
			int num3 = (int)num2;
			Interlocked.Add(ref _sealTailZeros, _ring.Read(_cursor, _pcm.AsSpan(0, num3 * 2), num3));
			_pcm.AsSpan(num3 * 2).Clear();
			if (EncodeAndWrite())
			{
				_cursor += 1024L;
			}
		}
	}

	public void BeginSeal(SystemAudioSealHandle handle)
	{
		_sealing = true;
		SampleMaxError();
		long drainStart = Stopwatch.GetTimestamp();
		AacEncoder encoder = _encoder;
		if (encoder != null)
		{
			for (int i = 0; i < 8; i++)
			{
				int num;
				try
				{
					num = encoder.Drain(_accessUnit);
				}
				catch (AacEncodeException)
				{
					break;
				}
				if (num <= 0 || !WriteAccessUnit(num))
				{
					break;
				}
			}
			encoder.Dispose();
			_encoder = null;
		}
		_sealed = Counters.Read(_ring, _captureCounters).Since(_opened);
		long num2 = Volatile.Read(in _end);
		long num3 = Interlocked.Read(in _frames);
		Volatile.Write(ref _sealOverrun, num3 * 1024 - ((num2 == long.MaxValue) ? 0 : (num2 - _anchorIndex)));
		_overrunKnown = true;
		string interruptedReason = _interruptedReason;
		string discardReason = null;
		if (num3 == 0L)
		{
			discardReason = interruptedReason ?? "no_packets";
		}
		else if (interruptedReason == null && _ring.ProducedUpTo <= _producedAtOpen)
		{
			discardReason = "no_packets";
		}
		Task.Run(() =>
		{
			CompleteSeal(handle, discardReason, drainStart);
		});
	}

	public void MarkInterrupted(string reason, int hr)
	{
		if (Interlocked.CompareExchange(ref _interruptedReason, reason, null) == null)
		{
			Volatile.Write(ref _interruptedAtMs, _anchorWallMs + (Stopwatch.GetTimestamp() - _anchorQpc) * 1000 / Stopwatch.Frequency);
			Log.Capture($"system audio interrupted seg={_segmentIndex} reason={reason} hr=0x{hr:X8}", "MarkInterrupted", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\shared\\dotnet\\SystemAudio\\AudioSegmentWriter.cs", 297);
		}
	}

	public SystemAudioFacts LiveSnapshot()
	{
		string interruptedReason = _interruptedReason;
		return Snapshot((interruptedReason == null) ? "on" : "interrupted", interruptedReason);
	}

	public SystemAudioFacts TimedOutSnapshot()
	{
		return Snapshot("failed", "finalize_timeout") with
		{
			File = null
		};
	}

	private void CompleteSeal(SystemAudioSealHandle handle, string? discardReason, long drainStart)
	{
		string filePath = null;
		SystemAudioFacts systemAudioFacts;
		try
		{
			if (discardReason != null)
			{
				_writer.Dispose();
				DeletePartialFile();
				MediaFoundationLifetime.Release();
				systemAudioFacts = Snapshot("failed", discardReason);
			}
			else
			{
				IMFSinkWriter writer = _writer;
				int streamIndex = _streamIndex;
				Exception error;
				switch (BoundedDrain.Run(() =>
				{
					writer.NotifyEndOfSegment(streamIndex);
					writer.Finalize();
				}, FinalizeTimeout, out error))
				{
				case BoundedDrainOutcome.Ok:
				{
					_finalizedPath = _path;
					filePath = _path;
					string interruptedReason = _interruptedReason;
					systemAudioFacts = Snapshot((interruptedReason == null) ? "on" : "interrupted", interruptedReason);
					long value = (Stopwatch.GetTimestamp() - drainStart) * 1000 / Stopwatch.Frequency;
					Log.Local($"system audio finalized {_fileName} frames={systemAudioFacts.Frames} drain_ms={value}", "CompleteSeal", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\shared\\dotnet\\SystemAudio\\AudioSegmentWriter.cs", 345);
					writer.Dispose();
					MediaFoundationLifetime.Release();
					break;
				}
				case BoundedDrainOutcome.Wedged:
					try
					{
						SystemAudioRuntime.WriterLeaked?.Invoke();
					}
					catch
					{
					}
					Log.Capture($"finalize TIMED OUT {_fileName} frames={Interlocked.Read(in _frames)} — leaking wedged writer", "CompleteSeal", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\shared\\dotnet\\SystemAudio\\AudioSegmentWriter.cs", 358);
					systemAudioFacts = Snapshot("failed", "finalize_timeout");
					break;
				default:
					systemAudioFacts = Snapshot("failed", $"finalize_failed:0x{error?.HResult ?? 0:X8}");
					writer.Dispose();
					DeletePartialFile();
					MediaFoundationLifetime.Release();
					break;
				}
			}
		}
		catch (Exception ex)
		{
			systemAudioFacts = Snapshot("failed", $"finalize_failed:0x{ex.HResult:X8}");
			filePath = null;
		}
		handle.RecordOutcome(systemAudioFacts.State == "on", systemAudioFacts.Reason);
		handle.Complete(systemAudioFacts, filePath);
	}

	private void SampleMaxError()
	{
		if (!_maxErrorFrozen)
		{
			long maxAbsErrorUs = _ring.MaxAbsErrorUs;
			if (maxAbsErrorUs > Volatile.Read(in _maxAbsErrorUs))
			{
				Volatile.Write(ref _maxAbsErrorUs, maxAbsErrorUs);
			}
		}
	}

	private bool EncodeAndWrite()
	{
		CountSound();
		int bytes;
		try
		{
			bytes = _encoder.Encode(_pcm, _accessUnit);
		}
		catch (AacEncodeException ex)
		{
			MarkInterrupted($"write_failed:0x{ex.Error:X4}", 0);
			_stopFeeding = true;
			return false;
		}
		return WriteAccessUnit(bytes);
	}

	private bool WriteAccessUnit(int bytes)
	{
		if (bytes <= 0)
		{
			return true;
		}
		try
		{
			long num = Interlocked.Read(in _frames);
			using IMFMediaBuffer iMFMediaBuffer = MediaFactory.MFCreateMemoryBuffer(bytes);
			iMFMediaBuffer.Lock(out var ppbBuffer, out var _, out var _);
			try
			{
				Marshal.Copy(_accessUnit, 0, ppbBuffer, bytes);
				iMFMediaBuffer.CurrentLength = bytes;
			}
			finally
			{
				iMFMediaBuffer.Unlock();
			}
			using IMFSample iMFSample = MediaFactory.MFCreateSample();
			iMFSample.AddBuffer(iMFMediaBuffer);
			iMFSample.SampleTime = SampleTimeHns(num);
			iMFSample.SampleDuration = SampleTimeHns(num + 1) - SampleTimeHns(num);
			_writer.WriteSample(_streamIndex, iMFSample);
			Interlocked.Increment(ref _frames);
			return true;
		}
		catch (Exception ex)
		{
			MarkInterrupted($"write_failed:0x{ex.HResult:X8}", ex.HResult);
			_stopFeeding = true;
			return false;
		}
	}

	private void CountSound()
	{
		long num = _cursor - _anchorIndex;
		for (int i = 0; i < 1024; i++)
		{
			if (Math.Abs((int)_pcm[2 * i]) > 32 || Math.Abs((int)_pcm[2 * i + 1]) > 32)
			{
				long num2 = (num + i) / 48000;
				if (num2 != _lastSoundSecond)
				{
					_lastSoundSecond = num2;
					Interlocked.Increment(ref _soundSeconds);
				}
				i = (int)Math.Min(1024L, (num2 + 1) * 48000 - num) - 1;
			}
		}
	}

	private static long SampleTimeHns(long accessUnit)
	{
		return (accessUnit * 1024 * 10000000 + 24000) / 48000;
	}

	private SystemAudioFacts Snapshot(string state, string? reason)
	{
		(SystemAudioMixFormat, bool) tuple = _streamFacts();
		SystemAudioMixFormat item = tuple.Item1;
		bool item2 = tuple.Item2;
		Counters counters = _sealed ?? Counters.Read(_ring, _captureCounters).Since(_opened);
		long num = Volatile.Read(in _endWallMs);
		string finalizedPath = _finalizedPath;
		string interruptedReason = _interruptedReason;
		return new SystemAudioFacts
		{
			State = state,
			Reason = reason,
			File = ((finalizedPath == null) ? null : _fileName),
			Source = "endpoint_loopback",
			Codec = "aac_lc",
			Encoder = _encoderVersion,
			SampleRate = 48000,
			Channels = 2,
			Bitrate = 256000,
			BandwidthHz = 20000,
			Frames = Interlocked.Read(in _frames),
			SoundMs = Math.Min(Interlocked.Read(in _soundSeconds) * 1000, AudioClock.FramesToMs(Interlocked.Read(in _frames) * 1024)),
			DelaySamples = _delay,
			AnchorWallMs = _anchorWallMs,
			EndWallMs = ((num >= 0) ? new long?(num) : ((long?)null)),
			ZerosFilled = Interlocked.Read(in _zerosRead) + counters.ZerosFilled,
			SamplesDropped = counters.SamplesDropped,
			Corrections = counters.Corrections,
			MaxAbsErrorUs = Volatile.Read(in _maxAbsErrorUs),
			TimestampErrors = counters.TimestampErrors,
			Discontinuities = counters.Discontinuities,
			DeviceReopens = counters.DeviceReopens,
			DroppedMs = counters.DroppedMs,
			StarvedPeriods = counters.StarvedPeriods,
			MixFormat = item,
			Keepalive = item2,
			SealTailZeros = Interlocked.Read(in _sealTailZeros),
			SealOverrunSamples = (_overrunKnown ? new long?(Volatile.Read(in _sealOverrun)) : ((long?)null)),
			InterruptedAtMs = ((interruptedReason == null) ? ((long?)null) : new long?(Volatile.Read(in _interruptedAtMs)))
		};
	}

	private static IMFMediaType CreateAacType(byte[] audioSpecificConfig)
	{
		IMFMediaType iMFMediaType = MediaFactory.MFCreateMediaType();
		iMFMediaType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio);
		iMFMediaType.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Aac);
		SetUInt32(iMFMediaType, MediaTypeAttributeKeys.AudioSamplesPerSecond, 48000u);
		SetUInt32(iMFMediaType, MediaTypeAttributeKeys.AudioNumChannels, 2u);
		SetUInt32(iMFMediaType, MediaTypeAttributeKeys.AudioBitsPerSample, 16u);
		SetUInt32(iMFMediaType, MediaTypeAttributeKeys.AudioAvgBytesPerSecond, 32000u);
		SetUInt32(iMFMediaType, MediaTypeAttributeKeys.AudioBlockAlignment, 1u);
		SetUInt32(iMFMediaType, MediaTypeAttributeKeys.AacPayloadType, 0u);
		SetUInt32(iMFMediaType, MediaTypeAttributeKeys.AacAudioProfileLevelIndication, 41u);
		SetUInt32(iMFMediaType, MediaTypeAttributeKeys.AllSamplesIndependent, 1u);
		byte[] array = new byte[12 + audioSpecificConfig.Length];
		array[2] = 41;
		audioSpecificConfig.CopyTo(array, 12);
		iMFMediaType.SetBlob(MediaTypeAttributeKeys.UserData, array);
		return iMFMediaType;
	}

	private static void SetUInt32(IMFAttributes attributes, Guid key, uint value)
	{
		attributes.Set(key, value);
	}

	private void DeletePartialFile()
	{
		try
		{
			if (File.Exists(_path))
			{
				File.Delete(_path);
			}
		}
		catch (Exception ex)
		{
			Log.Capture("system audio partial file cleanup failed " + _fileName + ": " + ex.Message, "DeletePartialFile", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\shared\\dotnet\\SystemAudio\\AudioSegmentWriter.cs", 592);
		}
	}
}
