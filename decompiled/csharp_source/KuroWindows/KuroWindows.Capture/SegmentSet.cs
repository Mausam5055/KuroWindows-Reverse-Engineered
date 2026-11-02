using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SystemAudio;
using ClientTelemetry;
using KuroWindows.Input;
using KuroWindows.RecorderCore.Capture;
using KuroWindows.RecorderCore.Clock;
using KuroWindows.RecorderCore.Segments;
using KuroWindows.Upload;

namespace KuroWindows.Capture;

internal sealed class SegmentSet : IDisposable
{
	private readonly string _sessionDir;

	private readonly SegmentManifestStore _manifestStore;

	private readonly SegmentManifest _manifest;

	private readonly List<SegmentWriter> _writers;

	private readonly RecorderWriteRouter? _router;

	private readonly SegmentEventFiles _files;

	private readonly object _sync = new object();

	private long _acceptFromWallMs = long.MinValue;

	private long _blockedFromWallMs = long.MaxValue;

	private int _sealed;

	private SegmentSessionFacts? _sessionFacts;

	private readonly ISystemAudio _systemAudio;

	private int _audioOpened;

	private long _audioAnchorQpc;

	private long _audioEndQpc;

	private const int BarrierWaitSliceMs = 100;

	public int SegmentIndex => _manifest.SegmentIndex;

	internal long StartedWallMs => _manifest.StartedWallMs;

	internal IReadOnlyList<SegmentDisplayManifest> Displays => _manifest.Displays;

	internal SegmentEventFiles Files => _files;

	internal SegmentWriteCounters Counters { get; } = new SegmentWriteCounters();

	public SegmentSet(string sessionDir, int segmentIndex, IEnumerable<DisplayRecorder> displays, FrameStateSource? frameState, DisplayTopologyPolicyResult displayPolicy, RecorderWriteRouter? router, SegmentOpenReason openReason, long clockCorrectionMs, Action<Exception>? captureFreshnessFailed = null, ISystemAudio? systemAudio = null)
		: this(sessionDir, segmentIndex, displayPolicy, router, openReason, clockCorrectionMs, () => displays.Select((DisplayRecorder display) => display.CreateSegmentWriter(sessionDir, segmentIndex, frameState, captureFreshnessFailed)), systemAudio)
	{
	}

	internal SegmentSet(string sessionDir, int segmentIndex, IReadOnlyList<SegmentWriter> writers, DisplayTopologyPolicyResult displayPolicy, RecorderWriteRouter? router)
		: this(sessionDir, segmentIndex, displayPolicy, router, SegmentOpenReason.Fresh, 0L, () => writers)
	{
	}

	private SegmentSet(string sessionDir, int segmentIndex, DisplayTopologyPolicyResult displayPolicy, RecorderWriteRouter? router, SegmentOpenReason openReason, long clockCorrectionMs, Func<IEnumerable<SegmentWriter>> writers, ISystemAudio? systemAudio = null)
	{
		_sessionDir = sessionDir;
		_router = router;
		_systemAudio = systemAudio ?? NullSystemAudio.Instance;
		_manifestStore = new SegmentManifestStore(sessionDir, segmentIndex);
		_manifest = new SegmentManifest
		{
			State = SegmentManifestState.Recording,
			SessionId = Path.GetFileName(sessionDir),
			SegmentIndex = segmentIndex,
			StartedWallMs = (long)RecordingClock.Shared.NowMs(),
			TopologyMode = displayPolicy.Mode.ToString().ToLowerInvariant(),
			TopologyEpoch = displayPolicy.TopologyEpoch,
			TopologySignature = displayPolicy.TopologySignature,
			PhysicalDisplayCount = displayPolicy.PhysicalDisplayCount,
			LogicalRecordedDisplayCount = displayPolicy.LogicalDisplayCount,
			TopologyUnsupportedReason = displayPolicy.UnsupportedReason,
			AdaptiveProfile = displayPolicy.Profile.Name,
			AdaptiveProfileIndex = displayPolicy.Profile.Index,
			AdaptiveProfileReason = displayPolicy.AdaptiveProfileReason,
			AdaptiveProfileFailureCount = displayPolicy.AdaptiveProfileFailureCount
		};
		_writers = new List<SegmentWriter>();
		SegmentEventFiles segmentEventFiles = null;
		try
		{
			foreach (SegmentWriter item in writers())
			{
				_writers.Add(item);
			}
			_manifest.Displays.AddRange(_writers.Select((SegmentWriter w) => w.DisplayManifest));
			segmentEventFiles = (_files = new SegmentEventFiles(sessionDir, _manifest.Displays));
			foreach (SegmentWriter writer in _writers)
			{
				int displayIndex = writer.DisplayManifest.DisplayIndex;
				writer.FirstCommit = (long wallMs) =>
				{
					OnFirstCommit(displayIndex, wallMs);
				};
			}
			SegmentPreamble.Write(this, displayPolicy, openReason, clockCorrectionMs, router?.InputPlane, router);
			_manifestStore.Save(_manifest);
		}
		catch
		{
			foreach (SegmentWriter writer2 in _writers)
			{
				try
				{
					writer2.Dispose();
				}
				catch
				{
				}
			}
			try
			{
				segmentEventFiles?.Dispose();
			}
			catch
			{
			}
			throw;
		}
	}

	internal SystemAudioFacts? QaAudio()
	{
		return _systemAudio.LiveFacts(_manifest.SegmentIndex);
	}

	public void SetSessionFacts(SegmentSessionFacts? facts)
	{
		Volatile.Write(ref _sessionFacts, facts);
	}

	public void AcceptWritesFrom(long wallMs)
	{
		lock (_sync)
		{
			if (wallMs > _acceptFromWallMs)
			{
				Interlocked.Exchange(ref _acceptFromWallMs, wallMs);
			}
			if (Interlocked.CompareExchange(ref _audioOpened, 1, 0) == 0)
			{
				long num = Math.Max(wallMs, _manifest.StartedWallMs);
				_audioAnchorQpc = RecordingClock.Shared.QpcFromWallMs(num);
				_systemAudio.PostOpen(_manifest.SegmentIndex, num, _audioAnchorQpc);
			}
			foreach (SegmentWriter writer in _writers)
			{
				writer.AcceptCommitsFrom(wallMs);
			}
		}
	}

	public void BlockWritesFrom(long wallMs)
	{
		lock (_sync)
		{
			if (wallMs < _blockedFromWallMs)
			{
				Interlocked.Exchange(ref _blockedFromWallMs, wallMs);
				Interlocked.Exchange(ref _audioEndQpc, RecordingClock.Shared.QpcFromWallMs(wallMs));
				_systemAudio.PostEnd(_manifest.SegmentIndex, wallMs, _audioEndQpc);
			}
			foreach (SegmentWriter writer in _writers)
			{
				writer.BlockCommitsFrom(wallMs);
			}
		}
	}

	public bool CanAcceptWallMs(long wallMs)
	{
		if (wallMs >= Interlocked.Read(in _acceptFromWallMs) && wallMs < Interlocked.Read(in _blockedFromWallMs))
		{
			return _sealed == 0;
		}
		return false;
	}

	public bool AcceptsEvent(long wallMs)
	{
		if (wallMs >= Interlocked.Read(in _acceptFromWallMs) && wallMs <= Interlocked.Read(in _blockedFromWallMs))
		{
			return !_files.Closed;
		}
		return false;
	}

	public CommitFrameResult CommitFrame(FrameCommitRequest frame)
	{
		if (!CanAcceptWallMs(frame.WallMs))
		{
			return CommitFrameResult.Blocked;
		}
		SegmentWriter segmentWriter;
		lock (_sync)
		{
			if (_sealed != 0)
			{
				return CommitFrameResult.Sealed;
			}
			segmentWriter = _writers.FirstOrDefault((SegmentWriter w) => w.DisplayManifest.DisplayIndex == frame.DisplayIndex);
		}
		if (segmentWriter == null)
		{
			return CommitFrameResult.Sealed;
		}
		if (segmentWriter.CommitFrame(frame))
		{
			return CommitFrameResult.Committed;
		}
		if (!CanAcceptWallMs(frame.WallMs))
		{
			return CommitFrameResult.Blocked;
		}
		return CommitFrameResult.Sealed;
	}

	internal void OnFirstCommit(int displayIndex, long wallMs)
	{
		EventRow item = new EventRow
		{
			Kind = RowKind.FrameZero,
			Ref = this,
			Display = displayIndex,
			WallMs = wallMs,
			OsTime = (uint)Environment.TickCount
		};
		if ((_router == null || !_router.TryEnqueueControl(in item)) && !_files.Closed)
		{
			long seq = EventSeq.Next();
			_files.Append(displayIndex, new RowSerializer().FrameZero(wallMs, displayIndex, seq, item.OsTime), seq);
		}
	}

	internal void WriteSegmentEndRows(RowSerializer serializer, long[] timestamps, string reason, string? maskTrigger, string? maskDetail)
	{
		uint tickCount = (uint)Environment.TickCount;
		for (int i = 0; i < _writers.Count && i < timestamps.Length; i++)
		{
			int displayIndex = _writers[i].DisplayManifest.DisplayIndex;
			long seq = EventSeq.Next();
			_files.Append(displayIndex, serializer.SegmentEnd(timestamps[i], reason, maskTrigger, maskDetail, _manifest.SegmentIndex, displayIndex, _files.Rows(displayIndex), _files.LastSeq(displayIndex), seq, tickCount), seq);
		}
	}

	public Task SealAsync(string endReason, string? maskTrigger = null, string? maskDetail = null, string? source = null, Exception? error = null, bool writeSegmentEnd = true, long? endedWallMs = null)
	{
		return Task.Run(() =>
		{
			using (Telemetry.Scope(_manifest.SessionId, _manifest.SegmentIndex))
			{
				if (Interlocked.Exchange(ref _sealed, 1) == 0)
				{
					long sealStartMs = RecordingClock.MonotonicMs();
					long timestamp = Stopwatch.GetTimestamp();
					long num = RecordingClock.Shared.WallMsFromQpc(timestamp);
					SystemAudioSealHandle handle = PostAudioSeal(endedWallMs, num, timestamp);
					_manifest.State = SegmentManifestState.Finalizing;
					_manifest.EndReason = endReason;
					_manifest.EndSource = source ?? SourceForReason(endReason);
					_manifest.MaskTrigger = maskTrigger;
					_manifest.MaskDetail = maskDetail;
					_manifest.EndError = ((error == null) ? null : new SegmentManifestError
					{
						Domain = error.GetType().FullName,
						Code = error.HResult,
						Name = error.GetType().Name,
						Description = error.Message
					});
					_manifestStore.Save(_manifest);
					Exception ex = CloseEventFiles(endReason, maskTrigger, maskDetail, writeSegmentEnd, num);
					TimeoutException ex2 = null;
					HashSet<int> hashSet = null;
					SegmentSessionFacts segmentSessionFacts = Volatile.Read(in _sessionFacts);
					SegmentInputFacts segmentInputFacts = segmentSessionFacts?.Input;
					if (segmentInputFacts != null)
					{
						segmentInputFacts.Late = Counters.Late;
						segmentInputFacts.Dropped = Counters.Dropped;
						segmentInputFacts.Thinned = Counters.Thinned;
						segmentInputFacts.QueuePeak = Counters.QueuePeak;
						segmentInputFacts.BlindMs = Counters.BlindMs;
						segmentInputFacts.Witness = Counters.Witness;
					}
					_manifest.SessionFacts = segmentSessionFacts;
					foreach (SegmentWriter writer in _writers)
					{
						try
						{
							writer.Seal();
						}
						catch (Exception ex3)
						{
							if (ex == null)
							{
								ex = ex3;
							}
							if (ex2 == null)
							{
								ex2 = AsFinalizeTimeout(ex3);
							}
							(hashSet ?? (hashSet = new HashSet<int>())).Add(writer.DisplayManifest.DisplayIndex);
							KuroLog.Capture($"segment writer seal failed seg={_manifest.SegmentIndex}: {ex3.Message}", "SealAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\SegmentSet.cs", 421);
						}
					}
					AwaitAudioSeal(handle, sealStartMs, endReason);
					if (ex != null && _manifest.EndError == null)
					{
						_manifest.EndError = new SegmentManifestError
						{
							Domain = ex.GetType().FullName,
							Code = ex.HResult,
							Name = ex.GetType().Name,
							Description = ex.Message
						};
					}
					FinalizeManifest(endReason, ex2, hashSet, endedWallMs);
				}
			}
		});
	}

	private SystemAudioSealHandle PostAudioSeal(long? endedWallMs, long sealNowWallMs, long nowQpc)
	{
		try
		{
			long num = Interlocked.Read(in _blockedFromWallMs);
			long num2;
			long endQpc;
			if (num != long.MaxValue)
			{
				num2 = Math.Min(num, endedWallMs ?? num);
				endQpc = Interlocked.Read(in _audioEndQpc) + (num2 - num) * Stopwatch.Frequency / 1000;
			}
			else
			{
				num2 = sealNowWallMs;
				endQpc = nowQpc;
			}
			return _systemAudio.PostSeal(_manifest.SegmentIndex, num2, endQpc);
		}
		catch (Exception ex)
		{
			KuroLog.Capture($"system audio seal post failed seg={_manifest.SegmentIndex}: {KuroLog.ExceptionSummary(ex)}", "PostAudioSeal", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\SegmentSet.cs", 464);
			return SystemAudioSealHandle.None;
		}
	}

	private void AwaitAudioSeal(SystemAudioSealHandle handle, long sealStartMs, string endReason)
	{
		try
		{
			long num = 2000L;
			long num2 = 20000L;
			long num3 = (SegmentEndReasons.AudioSealUsesFloor(endReason) ? num : Math.Max(num, num2 - (RecordingClock.MonotonicMs() - sealStartMs)));
			bool flag = handle.Wait(TimeSpan.FromMilliseconds(num3));
			SystemAudioFacts systemAudioFacts = (flag ? handle.Facts : handle.TimedOut());
			if (systemAudioFacts == null)
			{
				return;
			}
			SegmentAudioFacts segmentAudioFacts = ToManifestAudio(systemAudioFacts);
			_manifest.Audio = segmentAudioFacts;
			if (flag)
			{
				string filePath = handle.FilePath;
				if (filePath != null)
				{
					try
					{
						_manifest.AudioFile = Path.GetFileName(filePath);
						_manifest.AudioBytes = new FileInfo(filePath).Length;
						_manifest.AudioSha256 = UploadFileDigests.For(filePath).Sha256;
						segmentAudioFacts.File = _manifest.AudioFile;
					}
					catch (Exception ex)
					{
						_manifest.AudioFile = null;
						_manifest.AudioBytes = null;
						_manifest.AudioSha256 = null;
						segmentAudioFacts.File = null;
						KuroLog.Capture($"system audio file hash failed seg={_manifest.SegmentIndex}: {KuroLog.ExceptionSummary(ex)}", "AwaitAudioSeal", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\SegmentSet.cs", 505);
					}
				}
			}
			foreach (SegmentWriter writer in _writers)
			{
				long encodeStartQpc = writer.EncodeStartQpc;
				writer.DisplayManifest.AudioFirstFrameOffsetUs = ((encodeStartQpc == 0L) ? ((long?)null) : new long?(Math.Max(0L, (encodeStartQpc - _audioAnchorQpc) * 1000000 / Stopwatch.Frequency)));
			}
		}
		catch (Exception ex2)
		{
			KuroLog.Capture($"system audio seal wait failed seg={_manifest.SegmentIndex}: {KuroLog.ExceptionSummary(ex2)}", "AwaitAudioSeal", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\SegmentSet.cs", 519);
		}
	}

	private static SegmentAudioFacts ToManifestAudio(SystemAudioFacts f)
	{
		SegmentAudioFacts segmentAudioFacts = new SegmentAudioFacts
		{
			State = f.State,
			Reason = f.Reason,
			File = f.File,
			Source = f.Source,
			Codec = f.Codec,
			Encoder = f.Encoder,
			SampleRate = f.SampleRate,
			Channels = f.Channels,
			Bitrate = f.Bitrate,
			BandwidthHz = f.BandwidthHz,
			Frames = f.Frames,
			SoundMs = f.SoundMs,
			DelaySamples = f.DelaySamples,
			AnchorWallMs = f.AnchorWallMs,
			EndWallMs = f.EndWallMs,
			ZerosFilled = f.ZerosFilled,
			SamplesDropped = f.SamplesDropped,
			Corrections = f.Corrections,
			MaxAbsErrorUs = f.MaxAbsErrorUs,
			TimestampErrors = f.TimestampErrors,
			Discontinuities = f.Discontinuities,
			DeviceReopens = f.DeviceReopens,
			DroppedMs = f.DroppedMs,
			StarvedPeriods = f.StarvedPeriods
		};
		SystemAudioMixFormat mixFormat = f.MixFormat;
		segmentAudioFacts.MixFormat = (((object)mixFormat != null) ? new SegmentAudioMixFormat
		{
			Rate = mixFormat.Rate,
			Channels = mixFormat.Channels,
			Bits = mixFormat.Bits
		} : null);
		segmentAudioFacts.Keepalive = f.Keepalive;
		segmentAudioFacts.SealTailZeros = f.SealTailZeros;
		segmentAudioFacts.SealOverrunSamples = f.SealOverrunSamples;
		segmentAudioFacts.InterruptedAtMs = f.InterruptedAtMs;
		return segmentAudioFacts;
	}

	private static TimeoutException? AsFinalizeTimeout(Exception? error)
	{
		return (error as TimeoutException) ?? (error?.InnerException as TimeoutException);
	}

	private void FinalizeManifest(string endReason, TimeoutException? finalizeTimeout, HashSet<int>? failedDisplays, long? endedWallMs = null)
	{
		try
		{
			_manifest.EndReason = endReason;
			_manifest.EndedWallMs = endedWallMs ?? ((long)RecordingClock.Shared.NowMs());
			(QuarantineReason, string)? tuple = ((finalizeTimeout == null) ? (((QuarantineReason, string)?)null) : new (QuarantineReason, string)?((QuarantineReason.FinalizeTimeout, finalizeTimeout.Message)));
			string text = null;
			foreach (SegmentDisplayManifest display in _manifest.Displays)
			{
				bool flag = failedDisplays?.Contains(display.DisplayIndex) ?? false;
				if (!HasNonEmptyFile(display.VideoFile))
				{
					(QuarantineReason, string) valueOrDefault = tuple.GetValueOrDefault();
					if (!tuple.HasValue)
					{
						valueOrDefault = ((!flag) ? QuarantineReason.FileMissing : QuarantineReason.EncodeFailed, "missing or empty " + display.VideoFile);
						tuple = valueOrDefault;
					}
				}
				else if (!HasFinalizedMp4Container(display.VideoFile))
				{
					(QuarantineReason, string) valueOrDefault = tuple.GetValueOrDefault();
					if (!tuple.HasValue)
					{
						valueOrDefault = ((!flag) ? QuarantineReason.ContainerInvalid : QuarantineReason.EncodeFailed, "unfinalized or invalid MP4 container " + display.VideoFile);
						tuple = valueOrDefault;
					}
				}
				if (!HasNonEmptyFile(display.EventsFile))
				{
					(QuarantineReason, string) valueOrDefault = tuple.GetValueOrDefault();
					if (!tuple.HasValue)
					{
						valueOrDefault = ((!flag) ? QuarantineReason.FileMissing : QuarantineReason.EncodeFailed, "missing or empty " + display.EventsFile);
						tuple = valueOrDefault;
					}
				}
				if (string.IsNullOrWhiteSpace(display.FrameSidecarFile))
				{
					if (text == null)
					{
						text = $"missing frame sidecar for display {display.DisplayIndex}";
					}
					display.TimingSuspect = true;
					display.TimingSuspectReason = text;
					continue;
				}
				if (!HasNonEmptyFile(display.FrameSidecarFile))
				{
					if (text == null)
					{
						text = "missing or empty " + display.FrameSidecarFile;
					}
					display.TimingSuspect = true;
					display.TimingSuspectReason = text;
					continue;
				}
				ApplySidecarStats(display, Path.Combine(_sessionDir, display.FrameSidecarFile));
				if (display.EncodedFrameCount <= 0 && text == null)
				{
					text = "no committed frame rows in " + display.FrameSidecarFile;
				}
				long? num = TryReadEncoderFrameCount(display.VideoFile);
				if (!num.HasValue || num.GetValueOrDefault() <= 0)
				{
					if (text == null)
					{
						text = "missing encoder frame count for " + display.VideoFile;
					}
				}
				else if (num.Value != display.EncodedFrameCount && text == null)
				{
					text = $"sidecar/encoder frame count mismatch for display {display.DisplayIndex}: sidecar={display.EncodedFrameCount} encoder={num.Value}";
				}
				if (!string.IsNullOrWhiteSpace(display.SidecarIntegrityError) && text == null)
				{
					text = display.FrameSidecarFile + ": " + display.SidecarIntegrityError;
				}
				string text2 = SegmentManifestStore.CaptureSurfaceStaleReason(display);
				if (text2 != null)
				{
					display.CaptureSurfaceStale = true;
					display.CaptureSurfaceStaleReason = text2;
					KuroLog.Capture($"capture surface stale stats seg={_manifest.SegmentIndex} display={display.DisplayIndex} active_input_events={display.ActiveInputEventCount} source_frames={display.SourceFrameCount} cursor_only_frames={display.CursorOnlyFrameCount} duplicate_frames={display.DuplicateFrameCount} encoded_frames={display.EncodedFrameCount} unique_source_frame_hashes={display.UniqueSourceFrameHashCount} visual_change_score={display.SourceFrameVisualChangeScore} reason={text2}", "FinalizeManifest", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\SegmentSet.cs", 621);
				}
				if (text != null)
				{
					display.TimingSuspect = true;
					SegmentDisplayManifest segmentDisplayManifest = display;
					if (segmentDisplayManifest.TimingSuspectReason == null)
					{
						string text3 = (segmentDisplayManifest.TimingSuspectReason = text);
					}
				}
				KuroLog.LocalOnly("capture", $"writer commit stats seg={_manifest.SegmentIndex} display={display.DisplayIndex} encoded={display.EncodedFrameCount} source={display.SourceFrameCount} cursor_only={display.CursorOnlyFrameCount} duplicate={display.DuplicateFrameCount} degraded={display.DegradedFrameCount} unique_hashes={display.UniqueSourceFrameHashCount} input_events={display.ActiveInputEventCount}", "FinalizeManifest", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\SegmentSet.cs", 636);
			}
			SegmentSessionFacts sessionFacts = _manifest.SessionFacts;
			if (sessionFacts != null)
			{
				SegmentInputFacts input = sessionFacts.Input;
				KuroLog.LocalOnly("capture", $"session facts seg={_manifest.SegmentIndex} events={input?.Events ?? 0} injected={input?.Injected ?? 0} injected_mouse={input?.InjectedMouse ?? 0} injected_key={input?.InjectedKey ?? 0} raw_packets={input?.RawPackets ?? 0} raw_null_device={input?.RawNullDevice ?? 0} raw_absolute={input?.RawAbsolute ?? 0} devices={(input?.Devices?.Count).GetValueOrDefault()} extra_info={(input?.ExtraInfo?.Count).GetValueOrDefault()} remote={((sessionFacts.Session == null) ? "-" : (sessionFacts.Session.Remote ? "true" : "false"))} connect_state={sessionFacts.Session?.ConnectState ?? "-"} remote_tools={(sessionFacts.Session?.RemoteTools?.Count).GetValueOrDefault()} displays={sessionFacts.Displays?.Count ?? 0}", "FinalizeManifest", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\SegmentSet.cs", 648);
			}
			SegmentAudioFacts audio = _manifest.Audio;
			if (audio != null)
			{
				KuroLog.LocalOnly("capture", $"system audio seg={_manifest.SegmentIndex} state={audio.State} reason={audio.Reason ?? "-"} frames={audio.Frames.GetValueOrDefault()} zeros={audio.ZerosFilled.GetValueOrDefault()} dropped={audio.SamplesDropped.GetValueOrDefault()} corrections={audio.Corrections.GetValueOrDefault()} max_err_us={audio.MaxAbsErrorUs.GetValueOrDefault()} reopens={audio.DeviceReopens.GetValueOrDefault()} tail_zeros={audio.SealTailZeros.GetValueOrDefault()}", "FinalizeManifest", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\SegmentSet.cs", 662);
			}
			long? num2 = (from v in _manifest.Displays.Select(GetEncodedDurationMs)
				where v.HasValue && v.GetValueOrDefault() > 0
				select v).DefaultIfEmpty().Max();
			_manifest.ClaimedDurationMs = ((num2.HasValue && num2.GetValueOrDefault() > 0) ? num2.Value : EstimateDurationFromEventsOrFrames());
			string text5 = _manifest.Displays.Select((SegmentDisplayManifest d) => d.CaptureSurfaceStaleReason).FirstOrDefault((string reason) => !string.IsNullOrWhiteSpace(reason));
			if (tuple.HasValue)
			{
				(QuarantineReason, string) valueOrDefault2 = tuple.GetValueOrDefault();
				SegmentFate.Quarantine(_sessionDir, _manifest.SegmentIndex, _manifest, valueOrDefault2.Item1, FateOrigin.Seal, valueOrDefault2.Item2);
			}
			else if (text != null)
			{
				foreach (SegmentDisplayManifest item in _manifest.Displays.Where((SegmentDisplayManifest d) => d.TimingSuspectReason == null))
				{
					item.TimingSuspect = true;
					item.TimingSuspectReason = text;
				}
				_manifest.State = SegmentManifestState.Suspect;
				_manifest.QuarantineReason = null;
				_manifestStore.Save(_manifest);
				KuroLog.Capture($"segment suspect seg={_manifest.SegmentIndex}: {text}", "FinalizeManifest", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\SegmentSet.cs", 699);
				SegmentFate.EmitSealed(_manifest, FateOrigin.Seal);
				Telemetry.Emit(Ev.SegmentSuspect(_manifest.SessionId, _manifest.SegmentIndex, text));
			}
			else if (text5 != null)
			{
				SegmentManifestStore.SaveCaptureFreshnessSuspect(_sessionDir, _manifest.SegmentIndex, _manifest);
				KuroLog.Capture($"segment suspect seg={_manifest.SegmentIndex}: capture_freshness_stale reason={text5}", "FinalizeManifest", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\SegmentSet.cs", 711);
				SegmentFate.EmitSealed(_manifest, FateOrigin.Seal);
				Telemetry.Emit(Ev.SegmentSuspect(_manifest.SessionId, _manifest.SegmentIndex, "capture_freshness_stale:" + text5));
			}
			else
			{
				_manifest.State = SegmentManifestState.Complete;
				_manifestStore.Save(_manifest);
				KuroLog.Capture($"segment finalize complete seg={_manifest.SegmentIndex} duration_ms={_manifest.ClaimedDurationMs} displays={_manifest.Displays.Count}", "FinalizeManifest", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\SegmentSet.cs", 720);
				SegmentFate.EmitSealed(_manifest, FateOrigin.Seal);
			}
		}
		catch (Exception ex)
		{
			SegmentFate.Quarantine(_sessionDir, _manifest.SegmentIndex, _manifest, QuarantineReason.ManifestWriteFailed, FateOrigin.Seal, KuroLog.ExceptionSummary(ex));
		}
	}

	private bool HasNonEmptyFile(string fileName)
	{
		try
		{
			FileInfo fileInfo = new FileInfo(Path.Combine(_sessionDir, fileName));
			return fileInfo.Exists && fileInfo.Length > 0;
		}
		catch
		{
			return false;
		}
	}

	private bool HasFinalizedMp4Container(string fileName)
	{
		try
		{
			return Mp4ContainerInspector.HasFinalizedContainer(Path.Combine(_sessionDir, fileName));
		}
		catch (Exception ex)
		{
			KuroLog.Capture($"mp4 container inspection failed seg={_manifest.SegmentIndex} file={fileName}: {ex.Message}", "HasFinalizedMp4Container", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\SegmentSet.cs", 757);
			return false;
		}
	}

	private long EstimateDurationFromEventsOrFrames()
	{
		long? num = (from d in _manifest.Displays
			select TryReadEventSpanMs(d.EventsFile) into v
			where v.HasValue && v.GetValueOrDefault() > 0
			select v).DefaultIfEmpty().Max();
		if (num.HasValue && num.GetValueOrDefault() > 0)
		{
			return Math.Max(1L, num.Value);
		}
		return Math.Max(1L, (from d in _manifest.Displays
			select (d.EncodedFrameCount <= 0) ? ((long?)null) : new long?(Math.Max(1L, d.EncodedFrameCount * 1000 / Math.Max(d.CaptureFps, 1))) into v
			where v.HasValue && v.GetValueOrDefault() > 0
			select v).DefaultIfEmpty().Max() ?? 1);
	}

	private long? TryReadEventSpanMs(string eventsFile)
	{
		return ReadEventSpanMs(Path.Combine(_sessionDir, eventsFile));
	}

	internal static long? ReadEventSpanMs(string path)
	{
		try
		{
			if (!File.Exists(path))
			{
				return null;
			}
			double? num = null;
			double? num2 = null;
			foreach (string item in File.ReadLines(path))
			{
				if (string.IsNullOrWhiteSpace(item))
				{
					continue;
				}
				try
				{
					using JsonDocument jsonDocument = JsonDocument.Parse(item);
					if (jsonDocument.RootElement.TryGetProperty("timestamp", out var value) && value.TryGetDouble(out var value2) && (num.HasValue || !jsonDocument.RootElement.TryGetProperty("type", out var value3) || value3.ValueKind != JsonValueKind.String || !SegmentPreamble.Types.Contains(value3.GetString() ?? "")))
					{
						double valueOrDefault = num.GetValueOrDefault();
						if (!num.HasValue)
						{
							valueOrDefault = value2;
							num = valueOrDefault;
						}
						num2 = value2;
					}
				}
				catch
				{
				}
			}
			return (num.HasValue && num2.HasValue && num2.Value >= num.Value) ? new long?((long)Math.Round(num2.Value - num.Value)) : ((long?)null);
		}
		catch
		{
			return null;
		}
	}

	internal static long? GetEncodedDurationMs(SegmentDisplayManifest display)
	{
		int num = ((display.CaptureFps > 0) ? display.CaptureFps : 30);
		if (display.FirstPtsNs.HasValue && display.LastPtsNs.HasValue && display.LastPtsNs.Value >= display.FirstPtsNs.Value)
		{
			long? lastFrameDurationNs = display.LastFrameDurationNs;
			long num2 = ((lastFrameDurationNs.HasValue && lastFrameDurationNs.GetValueOrDefault() > 0) ? display.LastFrameDurationNs.Value : (1000000000L / (long)num));
			long num3 = display.LastPtsNs.Value - display.FirstPtsNs.Value + num2;
			return Math.Max(1L, (num3 + 999999) / 1000000);
		}
		if (display.EncodedFrameCount <= 0)
		{
			return null;
		}
		return Math.Max(1L, (display.EncodedFrameCount * 1000 + num - 1) / num);
	}

	internal static void ApplySidecarStats(SegmentDisplayManifest display, string sidecarPath)
	{
		if (!File.Exists(sidecarPath))
		{
			return;
		}
		long? firstFrameWallMs = null;
		long? lastFrameWallMs = null;
		long? firstPtsNs = null;
		long? lastPtsNs = null;
		long? lastFrameDurationNs = null;
		long num = 0L;
		long num2 = 0L;
		long num3 = 0L;
		long num4 = 0L;
		long num5 = 0L;
		long num6 = 0L;
		long num7 = 0L;
		long num8 = 0L;
		long num9 = 0L;
		long num10 = 0L;
		foreach (string item in File.ReadLines(sidecarPath))
		{
			if (string.IsNullOrWhiteSpace(item))
			{
				continue;
			}
			try
			{
				using JsonDocument jsonDocument = JsonDocument.Parse(item);
				JsonElement rootElement = jsonDocument.RootElement;
				if (!rootElement.TryGetProperty("frame", out var value) || !value.TryGetInt64(out var value2) || !rootElement.TryGetProperty("wall_ms", out var value3) || !value3.TryGetInt64(out var value4) || !rootElement.TryGetProperty("pts_ns", out var value5) || !value5.TryGetInt64(out var value6))
				{
					display.SidecarIntegrityError = "missing required frame, wall_ms, or pts_ns field";
					continue;
				}
				if (value2 != num10)
				{
					display.SidecarIntegrityError = $"non-contiguous frame index: expected={num10} actual={value2}";
					num10 = ((value2 >= num10) ? (value2 + 1) : (num10 + 1));
				}
				else
				{
					num10++;
				}
				if (lastFrameWallMs.HasValue && value4 < lastFrameWallMs.Value)
				{
					display.SidecarIntegrityError = $"wall_ms regressed at frame {value2}";
				}
				if (lastPtsNs.HasValue && value6 <= lastPtsNs.Value)
				{
					display.SidecarIntegrityError = $"pts_ns did not strictly increase at frame {value2}";
				}
				bool flag = rootElement.TryGetProperty("timestamp_kind", out var value7) && value7.ValueKind == JsonValueKind.String && value7.GetString() == "duplicate_qpc";
				if (flag)
				{
					num5++;
				}
				string text = null;
				if (rootElement.TryGetProperty("frame_kind", out var value8) && value8.ValueKind == JsonValueKind.String)
				{
					text = value8.GetString();
				}
				if (text == "source")
				{
					num8++;
					if (!rootElement.TryGetProperty("timestamp_quality", out var value9) || value9.ValueKind != JsonValueKind.String || !(value9.GetString() == "authoritative"))
					{
						num9++;
					}
				}
				if (rootElement.TryGetProperty("duration_ns", out var value10) && value10.TryGetInt64(out var value11) && value11 > 0)
				{
					lastFrameDurationNs = value11;
					if (flag)
					{
						num6 += value11;
					}
					else
					{
						num7 += value11;
					}
					switch (text)
					{
					case "source":
						num2 += value11;
						break;
					case "cursor_only":
						num3 += value11;
						break;
					case "duplicate":
						num4 += value11;
						break;
					}
				}
				long valueOrDefault = firstFrameWallMs.GetValueOrDefault();
				if (!firstFrameWallMs.HasValue)
				{
					valueOrDefault = value4;
					firstFrameWallMs = valueOrDefault;
				}
				lastFrameWallMs = value4;
				valueOrDefault = firstPtsNs.GetValueOrDefault();
				if (!firstPtsNs.HasValue)
				{
					valueOrDefault = value6;
					firstPtsNs = valueOrDefault;
				}
				lastPtsNs = value6;
				num++;
			}
			catch
			{
				display.SidecarIntegrityError = "truncated or invalid JSONL row";
			}
		}
		display.FirstFrameWallMs = firstFrameWallMs;
		display.LastFrameWallMs = lastFrameWallMs;
		display.FirstPtsNs = firstPtsNs;
		display.LastPtsNs = lastPtsNs;
		display.LastFrameDurationNs = lastFrameDurationNs;
		display.EncodedFrameCount = num;
		SegmentDisplayManifest segmentDisplayManifest = display;
		string? timestampQuality;
		if (num <= 0)
		{
			timestampQuality = null;
		}
		else
		{
			timestampQuality = ((num8 > 0 && num9 == 0L) ? "authoritative" : "degraded");
		}
		segmentDisplayManifest.TimestampQuality = timestampQuality;
		display.SourceFrameDurationNs = num2;
		display.CursorOnlyFrameDurationNs = num3;
		display.DuplicateFrameDurationNs = num4;
		display.FabricatedFrameCount = num5;
		display.FabricatedFrameDurationNs = num6;
		display.NonFabricatedFrameCount = num - num5;
		display.NonFabricatedFrameDurationNs = num7;
	}

	private long? TryReadEncoderFrameCount(string videoFile)
	{
		try
		{
			string path = Path.ChangeExtension(Path.Combine(_sessionDir, videoFile), ".frames");
			if (!File.Exists(path))
			{
				return null;
			}
			long result;
			return long.TryParse(File.ReadAllText(path).Trim(), out result) ? new long?(result) : ((long?)null);
		}
		catch
		{
			return null;
		}
	}

	private Exception? CloseEventFiles(string reason, string? maskTrigger, string? maskDetail, bool writeSegmentEnd, long nowWallMs)
	{
		SealBarrier sealBarrier = new SealBarrier(this);
		long[] array = null;
		if (writeSegmentEnd)
		{
			long num = EffectiveSegmentEndMaxWallMs();
			long fallbackWallMs = ((num == long.MaxValue) ? nowWallMs : num);
			array = new long[_writers.Count];
			for (int i = 0; i < _writers.Count; i++)
			{
				array[i] = _writers[i].EffectiveEndWallMs(fallbackWallMs, num);
			}
		}
		EventRow item = ((array == null) ? new EventRow
		{
			Kind = RowKind.Barrier,
			Ref = sealBarrier
		} : new EventRow
		{
			Kind = RowKind.SegmentEnd,
			Ref = new SegmentEndItem(this, array, reason, maskTrigger, maskDetail, sealBarrier)
		});
		if (_router != null && _router.TryEnqueueControl(in item))
		{
			if (!WaitForBarrier(sealBarrier))
			{
				KuroLog.LocalOnly("capture", $"segment seal event barrier timed out seg={_manifest.SegmentIndex}; event files leaked", "CloseEventFiles", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\SegmentSet.cs", 1051);
				return null;
			}
			return sealBarrier.Error;
		}
		try
		{
			if (array != null && !_files.Closed)
			{
				WriteSegmentEndRows(new RowSerializer(), array, reason, maskTrigger, maskDetail);
			}
			_files.Close();
			return null;
		}
		catch (Exception result)
		{
			return result;
		}
	}

	private static bool WaitForBarrier(SealBarrier barrier)
	{
		long num = 20000000L;
		long num2 = PipelineWatchdog.ReadUnbiasedInterruptTimeHns();
		while (!barrier.Done.Wait(100))
		{
			if (PipelineWatchdog.ReadUnbiasedInterruptTimeHns() - num2 >= num)
			{
				return false;
			}
		}
		return true;
	}

	private long EffectiveSegmentEndMaxWallMs()
	{
		long num = Interlocked.Read(in _blockedFromWallMs);
		if (num != long.MaxValue)
		{
			return Math.Max(_manifest.StartedWallMs, num - 1);
		}
		return long.MaxValue;
	}

	private static string SourceForReason(string reason)
	{
		return reason switch
		{
			"timer_rotation" => "segment_timer", 
			"idle" => "windows_idle_manager", 
			"system_sleep" => "windows_idle_manager", 
			"screen_lock" => "windows_idle_manager", 
			"session_logoff" => "windows_idle_manager", 
			"session_disconnect" => "windows_idle_manager", 
			"privacy_mask" => "windows_privacy_manager", 
			"low_disk" => "windows_disk_monitor", 
			"force_update" => "kuro_update_required", 
			"capture_failure" => "windows_capture", 
			"capture_access_denied" => "windows_capture", 
			"app_quit" => "windows_app_lifecycle", 
			"user_stop" => "windows_user", 
			_ => "windows_app", 
		};
	}

	public void Dispose()
	{
		SealAsync("app_quit");
	}
}
