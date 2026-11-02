using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using ClientTelemetry;
using KuroWindows.Input;
using KuroWindows.RecorderCore.Capture;
using KuroWindows.RecorderCore.Clock;
using KuroWindows.RecorderCore.Diagnostics;
using KuroWindows.RecorderCore.Encode;
using KuroWindows.RecorderCore.Segments;

namespace KuroWindows.Capture;

internal sealed class SegmentWriter : IDisposable
{
	private const long MinPresentationStepHns = 10000L;

	private const long ActiveInputSlowEncodeThresholdMs = 1000L;

	private readonly object _commitSync = new object();

	private readonly FrameStateSource? _frameState;

	private readonly ISegmentVideoEncoder _encoder;

	private readonly FrameSidecarWriter? _sidecarWriter;

	private readonly SegmentDisplayManifest _display;

	private readonly HashSet<ulong> _sourceFrameHashes = new HashSet<ulong>();

	private readonly CaptureFreshnessGuard _freshnessGuard;

	private readonly Action<Exception>? _captureFreshnessFailed;

	private readonly int _segmentIndex;

	private readonly string _sessionId;

	private readonly LatencyHistogram _encodeLatency = new LatencyHistogram();

	private long _acceptFromWallMs = long.MinValue;

	private long _blockedFromWallMs = long.MaxValue;

	private long _encodeStartQpc;

	private long _lastPresentationHns = -1L;

	private long _prevFrameWallMs;

	private long _lastCommittedWallMs = long.MinValue;

	private long _lastCommittedDurationNs;

	private int _sealed;

	private int _freshnessFailureSignaled;

	public SegmentDisplayManifest DisplayManifest => _display;

	public long EncodeStartQpc => Interlocked.Read(in _encodeStartQpc);

	public Action<long>? FirstCommit { get; set; }

	public Action? Sealed { get; init; }

	public SegmentWriter(string sessionDir, int segmentIndex, CaptureDisplaySettings display, CaptureBackendKind backend, int width, int height, SegmentEncoderFactory encoderFactory, int captureFps, string fallbackMode, FrameStateSource? frameState, Action<Exception>? captureFreshnessFailed = null)
	{
		_segmentIndex = segmentIndex;
		_sessionId = Path.GetFileName(sessionDir);
		_frameState = frameState;
		_captureFreshnessFailed = captureFreshnessFailed;
		int logicalDisplayIndex = display.LogicalDisplayIndex;
		string text = $"clip_{logicalDisplayIndex}_{segmentIndex}.mp4";
		string eventsFile = $"events_{logicalDisplayIndex}_{segmentIndex}.json";
		string text2 = $"frame_timestamps_{logicalDisplayIndex}_{segmentIndex}.jsonl";
		_freshnessGuard = new CaptureFreshnessGuard(logicalDisplayIndex, captureFps);
		_encoder = encoderFactory(Path.Combine(sessionDir, text), width, height, captureFps);
		_sidecarWriter = ((frameState == null) ? null : new FrameSidecarWriter(Path.Combine(sessionDir, text2)));
		_display = new SegmentDisplayManifest
		{
			DisplayIndex = logicalDisplayIndex,
			OriginalScreenIndex = display.OriginalScreenIndex,
			DisplayRole = display.Role.ToString().ToLowerInvariant(),
			Backend = backend,
			VideoFile = text,
			EventsFile = eventsFile,
			FrameSidecarFile = ((frameState == null) ? null : text2),
			CaptureFps = captureFps,
			NativeWidth = display.NativeBounds.Width,
			NativeHeight = display.NativeBounds.Height,
			EncodedWidth = width,
			EncodedHeight = height,
			ResolutionMode = display.ResolutionMode,
			ResolutionThrottled = display.IsResolutionThrottled,
			BitrateBps = _encoder.TargetBitrate,
			EncoderBackend = _encoder.EncoderBackend,
			InputMemoryPath = _encoder.InputMemoryPath,
			HardwareTransformsRequested = _encoder.HardwareTransformsRequested,
			EncoderSelectionReason = _encoder.EncoderSelectionReason,
			FallbackMode = fallbackMode
		};
	}

	public bool CommitFrame(FrameCommitRequest frame)
	{
		Exception ex = null;
		lock (_commitSync)
		{
			if (Volatile.Read(in _sealed) != 0)
			{
				return false;
			}
			if (frame.WallMs < Interlocked.Read(in _acceptFromWallMs) || frame.WallMs >= Interlocked.Read(in _blockedFromWallMs))
			{
				return false;
			}
			TimestampQuality timestampQuality = frame.TimestampQuality;
			if (_encodeStartQpc == 0L)
			{
				Interlocked.Exchange(ref _encodeStartQpc, frame.SourceQpc);
				_prevFrameWallMs = frame.WallMs - 1;
				FirstCommit?.Invoke(frame.WallMs);
			}
			long num = (long)((double)(frame.SourceQpc - _encodeStartQpc) * 10000000.0 / (double)Stopwatch.Frequency);
			long num2 = frame.WallMs;
			if (_lastPresentationHns >= 0 && num <= _lastPresentationHns)
			{
				num = _lastPresentationHns + 10000;
				timestampQuality = TimestampQuality.Degraded;
			}
			if (num2 <= _prevFrameWallMs)
			{
				num2 = _prevFrameWallMs + 1;
				timestampQuality = TimestampQuality.Degraded;
			}
			int num3 = 10000000 / Math.Max(_display.CaptureFps, 1);
			long durationHns = ((_lastPresentationHns >= 0) ? Math.Max(10000L, num - _lastPresentationHns) : num3);
			EncodedFrameCommit commit = _encoder.WriteFrame(in frame, num, durationHns, num2, timestampQuality);
			_lastPresentationHns = commit.PtsNs / 100;
			_encodeLatency.Record(commit.EncodeElapsedMs);
			if (_sidecarWriter != null && _frameState != null)
			{
				FrameStateSnapshot frameStateSnapshot = _frameState.SnapshotForFrame(_prevFrameWallMs, num2);
				FrameStateSnapshot integritySnapshot = SnapshotForIntegrity(_prevFrameWallMs, num2, commit, frameStateSnapshot, frame.CursorScreen, out var integrityCursorScreen);
				ex = UpdateCaptureIntegrity(frame, commit, timestampQuality, frameStateSnapshot, integritySnapshot, integrityCursorScreen);
				_sidecarWriter.WriteCommit(commit, frame.Cursor.Hotspot.X, frame.Cursor.Hotspot.Y, frame.CursorScreen, frameStateSnapshot, frame.FramesDroppedSincePrev, frame.CursorBlendChanged, frame.Cursor);
			}
			else
			{
				ex = UpdateCaptureIntegrity(frame, commit, timestampQuality, null, null, frame.CursorScreen);
			}
			_prevFrameWallMs = num2;
			Interlocked.Exchange(ref _lastCommittedWallMs, num2);
			Interlocked.Exchange(ref _lastCommittedDurationNs, commit.DurationNs);
		}
		if (ex != null)
		{
			try
			{
				_captureFreshnessFailed?.Invoke(ex);
			}
			catch
			{
			}
		}
		return true;
	}

	private Exception? UpdateCaptureIntegrity(FrameCommitRequest frame, EncodedFrameCommit commit, TimestampQuality timestampQuality, FrameStateSnapshot? frameSnapshot, FrameStateSnapshot? integritySnapshot, int integrityCursorScreen)
	{
		if (timestampQuality == TimestampQuality.Degraded)
		{
			_display.DegradedFrameCount++;
		}
		switch (frame.FrameKind)
		{
		case CaptureFrameKind.Source:
		{
			_display.SourceFrameCount++;
			_display.SourceFrameDurationNs += commit.DurationNs;
			_sourceFrameHashes.Add(frame.FrameVisualHash);
			_display.UniqueSourceFrameHashCount = _sourceFrameHashes.Count;
			int? frameChangeScore = frame.FrameChangeScore;
			if (frameChangeScore.HasValue && frameChangeScore.GetValueOrDefault() > 0 && FrameVisualFingerprint.IsMeaningfulChange(frame.FrameChangeScore))
			{
				_display.SourceFrameVisualChangeScore += frame.FrameChangeScore.Value;
			}
			break;
		}
		case CaptureFrameKind.CursorOnly:
			_display.CursorOnlyFrameCount++;
			_display.CursorOnlyFrameDurationNs += commit.DurationNs;
			break;
		case CaptureFrameKind.Duplicate:
			_display.DuplicateFrameCount++;
			_display.DuplicateFrameDurationNs += commit.DurationNs;
			break;
		}
		int num = 0;
		if (frameSnapshot != null)
		{
			num = CountDisplayActiveInputEvents(frameSnapshot, _display.OriginalScreenIndex ?? frame.DisplayIndex, frame.CursorScreen);
			_display.ActiveInputEventCount += num;
		}
		int num2 = ((integritySnapshot != null) ? CountDisplayActiveInputEvents(integritySnapshot, _display.OriginalScreenIndex ?? frame.DisplayIndex, integrityCursorScreen) : num);
		bool flag = false;
		string text = SlowEncodeQualityReason(commit, num2, _display.DisplayIndex);
		if (text != null)
		{
			flag = true;
		}
		else
		{
			text = _freshnessGuard.Observe(frame.TimestampKind, num, commit.PtsNs, commit.DurationNs);
		}
		if (text != null && Interlocked.Exchange(ref _freshnessFailureSignaled, 1) == 0)
		{
			_display.CaptureSurfaceStale = true;
			_display.CaptureSurfaceStaleReason = text;
			KuroLog.Capture($"capture quality failure seg={_segmentIndex} display={_display.DisplayIndex} frame={commit.FrameIndex} frame_kind={commit.FrameKind} duration_ms={commit.DurationNs / 1000000} encode_elapsed_ms={commit.EncodeElapsedMs} active_input_events={num} integrity_active_input_events={num2} reason={text}", "UpdateCaptureIntegrity", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\SegmentWriter.cs", 286);
			if (flag)
			{
				Telemetry.Emit(Ev.EncoderStall(commit.EncodeElapsedMs, _sessionId, _segmentIndex, _display.DisplayIndex, true));
				CaptureEncoderStallException ex = new CaptureEncoderStallException(_display.DisplayIndex, text);
				if (ex.LatchesWireProfile && string.Equals(_encoder.WireProfile, "high", StringComparison.Ordinal))
				{
					H264WireProfilePolicy.LatchLegacy($"encode_stall seg={_segmentIndex} display={_display.DisplayIndex} encode_elapsed_ms={commit.EncodeElapsedMs}");
				}
				return ex;
			}
			return new CaptureSourceStarvationException(_display.DisplayIndex, text);
		}
		return null;
	}

	private FrameStateSnapshot SnapshotForIntegrity(long fromWallMsExclusive, long committedWallMs, EncodedFrameCommit commit, FrameStateSnapshot frameSnapshot, int frameCursorScreen, out int integrityCursorScreen)
	{
		integrityCursorScreen = frameCursorScreen;
		if (_frameState == null || commit.EncodeElapsedMs < 1000)
		{
			return frameSnapshot;
		}
		long num = (long)RecordingClock.Shared.NowMs();
		long num2 = Interlocked.Read(in _blockedFromWallMs);
		if (num2 != long.MaxValue)
		{
			num = Math.Min(num, num2 - 1);
		}
		if (num <= committedWallMs)
		{
			return frameSnapshot;
		}
		integrityCursorScreen = _frameState.MousePositionForFrame(num)?.Screen ?? frameCursorScreen;
		return _frameState.SnapshotForFrame(fromWallMsExclusive, num);
	}

	private static string? SlowEncodeQualityReason(EncodedFrameCommit commit, int displayActiveInputEvents, int displayIndex)
	{
		if (displayActiveInputEvents <= 0 || commit.EncodeElapsedMs < 1000)
		{
			return null;
		}
		return $"capture quality encoder stall for display {displayIndex}: encode_elapsed_ms={commit.EncodeElapsedMs} active_input_events={displayActiveInputEvents}";
	}

	internal static int CountDisplayActiveInputEvents(FrameStateSnapshot snapshot, int displayIndex, int cursorScreen)
	{
		return snapshot.Scroll.Count((ScrollEntry s) => s.Screen == displayIndex) + snapshot.Clicks.Count((ClickEntry c) => c.IsDown && c.Screen == displayIndex) + CountDisplayMouseMoves(snapshot.MousePositions, displayIndex) + snapshot.KeyTransitions.Count((KeyTransitionEntry k) => k.IsDown && (k.Screen == displayIndex || (!k.Screen.HasValue && cursorScreen == displayIndex)));
	}

	private static int CountDisplayMouseMoves(MousePositionEntry[] mousePositions, int displayIndex)
	{
		int num = 0;
		MousePositionEntry mousePositionEntry = null;
		foreach (MousePositionEntry mousePositionEntry2 in mousePositions)
		{
			if (mousePositionEntry2.Screen != displayIndex)
			{
				mousePositionEntry = null;
				continue;
			}
			if ((object)mousePositionEntry != null)
			{
				MousePositionEntry mousePositionEntry3 = mousePositionEntry;
				if (mousePositionEntry3.X != mousePositionEntry2.X || mousePositionEntry3.Y != mousePositionEntry2.Y)
				{
					num++;
				}
			}
			mousePositionEntry = mousePositionEntry2;
		}
		return num;
	}

	public long EffectiveEndWallMs(long fallbackWallMs, long maxWallMs = long.MaxValue)
	{
		long num = Interlocked.Read(in _lastCommittedWallMs);
		if (num == long.MinValue)
		{
			return Math.Min(fallbackWallMs, maxWallMs);
		}
		long num2 = Interlocked.Read(in _lastCommittedDurationNs);
		long num3 = ((num2 > 0) ? Math.Max(1L, (num2 + 999999) / 1000000) : Math.Max(1, 1000 / Math.Max(_display.CaptureFps, 1)));
		return Math.Min(num + num3, maxWallMs);
	}

	public void AcceptCommitsFrom(long wallMs)
	{
		long num = Interlocked.Read(in _acceptFromWallMs);
		while (wallMs > num)
		{
			long num2 = Interlocked.CompareExchange(ref _acceptFromWallMs, wallMs, num);
			if (num2 == num)
			{
				break;
			}
			num = num2;
		}
	}

	public void Seal()
	{
		if (Interlocked.Exchange(ref _sealed, 1) != 0)
		{
			return;
		}
		try
		{
			SealCore();
		}
		finally
		{
			try
			{
				Sealed?.Invoke();
			}
			catch
			{
			}
		}
	}

	private void SealCore()
	{
		if (!Monitor.TryEnter(_commitSync, TimeSpan.FromSeconds(20.0)))
		{
			KuroLog.Capture($"segment seal lock timed out seg={_segmentIndex} display={_display.DisplayIndex} " + "(wedged encoder commit)", "SealCore", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\SegmentWriter.cs", 477);
			LeakedWriterCounter.Increment();
			TimeoutException innerException = new TimeoutException($"segment seal lock timed out after {20}s " + "(wedged encoder commit); sidecar writer and encoder leaked");
			throw new InvalidOperationException("Segment writer seal failed.", innerException);
		}
		try
		{
			Exception ex = null;
			try
			{
				_sidecarWriter?.Dispose();
			}
			catch (Exception ex2)
			{
				if (ex == null)
				{
					ex = ex2;
				}
			}
			try
			{
				_encoder.Dispose();
			}
			catch (Exception ex3)
			{
				if (ex == null)
				{
					ex = ex3;
				}
			}
			long num = _encodeLatency.CountAtOrAbove(250L);
			long num2 = _encodeLatency.CountAtOrAbove(1000L);
			Observe.EncodeP95Ms(_encodeLatency.QuantileMs(0.95));
			Count.EncoderStall(EncoderStallLabel.Ge250ms, num - num2);
			Count.EncoderStall(EncoderStallLabel.Ge1000ms, num2);
			KuroLog.Capture($"encode latency summary seg={_segmentIndex} display={_display.DisplayIndex} encoder_backend={_display.EncoderBackend} frames={_encodeLatency.Count} p50_ms={_encodeLatency.QuantileMs(0.5)} p95_ms={_encodeLatency.QuantileMs(0.95)} max_ms={_encodeLatency.MaxMs} ge100={_encodeLatency.CountAtOrAbove(100L)} ge250={num} ge1000={num2} stale={_display.CaptureSurfaceStale.ToString().ToLowerInvariant()} finalize_ok={(_encoder.FinalizeError == null).ToString().ToLowerInvariant()}", "SealCore", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\SegmentWriter.cs", 511);
			if (ex != null)
			{
				throw new InvalidOperationException("Segment writer seal failed.", ex);
			}
			if (_encoder.FinalizeError != null)
			{
				throw new InvalidOperationException("Video encoder failed to finalize MP4 output.", _encoder.FinalizeError);
			}
		}
		finally
		{
			Monitor.Exit(_commitSync);
		}
	}

	public void BlockCommitsFrom(long wallMs)
	{
		long num = Interlocked.Read(in _blockedFromWallMs);
		while (wallMs < num)
		{
			long num2 = Interlocked.CompareExchange(ref _blockedFromWallMs, wallMs, num);
			if (num2 == num)
			{
				break;
			}
			num = num2;
		}
	}

	public void Dispose()
	{
		Seal();
	}
}
