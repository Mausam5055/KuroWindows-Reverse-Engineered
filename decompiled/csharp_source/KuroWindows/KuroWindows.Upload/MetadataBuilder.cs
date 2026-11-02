using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using DeviceIdentity;
using KuroWindows.RecorderCore.Segments;
using Windows.Storage;

namespace KuroWindows.Upload;

public static class MetadataBuilder
{
	private sealed record FrozenSnapshot(DateTimeOffset UploadedAt);

	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
	private struct MEMORYSTATUSEX
	{
		public uint dwLength;

		public uint dwMemoryLoad;

		public ulong ullTotalPhys;

		public ulong ullAvailPhys;

		public ulong ullTotalPageFile;

		public ulong ullAvailPageFile;

		public ulong ullTotalVirtual;

		public ulong ullAvailVirtual;

		public ulong ullAvailExtendedVirtual;
	}

	private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
	{
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
		WriteIndented = true,
		Converters = 
		{
			(JsonConverter)new Iso8601UtcConverter(),
			(JsonConverter)new NullableIso8601UtcConverter()
		}
	};

	private const long MaxClaimedDurationMs = 960000L;

	private const int MaxSessionFactsBytes = 2048;

	private static readonly JsonSerializerOptions CompactJsonOptions = new JsonSerializerOptions
	{
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
		WriteIndented = false
	};

	private static readonly HashSet<string> ActivityTypes = new HashSet<string> { "click", "drag", "scroll", "key", "modifier_change", "screen_transition" };

	private static readonly HashSet<string> PreambleTypes = new HashSet<string> { "header", "clock_anchor", "screen_config", "active_app", "input_settings", "touchpad_device", "input_state", "input_blind" };

	private const string TouchRowPrefix = "{\"type\":\"touch\",";

	private static readonly HashSet<string> JigglerBundleIds = new HashSet<string> { "com.sticktron.jiggler", "com.bhaller.Jiggler", "com.yuiz.mouse-jiggler", "de.lkraider.mouseshaker", "com.robhurring.keep-moving", "com.fadel.MouoMouse", "net.mostlygeek.MoveTheMouse" };

	private static readonly HashSet<string> JigglerExactNames = new HashSet<string> { "jiggler", "mouse jiggler", "mouse shaker", "jiggly mouse", "mouseshaker", "move mouse", "mouse mover", "movethemouse" };

	public static async Task<SegmentMetrics> BuildAsync(SegmentInfo seg, string osVersion, string machineModel)
	{
		SegmentManifest segmentManifest = SegmentManifestStore.TryLoad(SegmentManifestStore.ManifestPath(seg.SessionDir, seg.SegmentIndex));
		long num = GetClaimedDurationFromManifest(segmentManifest) ?? (await GetClaimedDurationMsAsync(seg.Displays).ConfigureAwait(continueOnCapturedContext: false));
		long durationMs = num;
		durationMs = Math.Clamp(durationMs, 1L, 960000L);
		DateTimeOffset? capturedAt = ((segmentManifest != null && segmentManifest.StartedWallMs > 0) ? new DateTimeOffset?(DateTimeOffset.FromUnixTimeMilliseconds(segmentManifest.StartedWallMs)) : ParseCapturedAt(seg.ClientSessionId));
		EventScanResult scan = ScanEvents(seg.Displays[0].EventsPath, durationMs);
		DateTimeOffset uploadedAt = DateTimeOffset.UtcNow;
		HashSet<int> existing = new HashSet<int>();
		bool hasSnapshot = false;
		foreach (DisplayFiles display in seg.Displays.OrderBy((DisplayFiles d) => d.ScreenIndex))
		{
			string path = Path.Combine(seg.SessionDir, $"metadata_{display.ScreenIndex}_{seg.SegmentIndex}.json");
			if (!File.Exists(path))
			{
				continue;
			}
			FrozenSnapshot frozenSnapshot = await ReadFrozenSnapshotAsync(path, seg, display, durationMs, scan.IdleDurationMs).ConfigureAwait(continueOnCapturedContext: false);
			if ((object)frozenSnapshot == null)
			{
				File.Delete(path);
				continue;
			}
			if (!hasSnapshot)
			{
				uploadedAt = frozenSnapshot.UploadedAt;
				hasSnapshot = true;
			}
			existing.Add(display.ScreenIndex);
		}
		List<SegmentFile> videoFiles = new List<SegmentFile>();
		List<SegmentFile> supportFiles = new List<SegmentFile>();
		string text = SegmentManifestStore.ManifestFileName(seg.SegmentIndex);
		string text2 = Path.Combine(seg.SessionDir, text);
		if (File.Exists(text2))
		{
			long length = new FileInfo(text2).Length;
			if (length > 0)
			{
				supportFiles.Add(new SegmentFile(text, length));
			}
		}
		SessionFactsMetadata sessionFacts = BuildSessionFactsMetadata(segmentManifest);
		AudioMetadata audio = BuildAudioMetadata(segmentManifest);
		SegmentFile audioFile = AudioFileIfPresent(seg.SessionDir, segmentManifest);
		foreach (DisplayFiles item in seg.Displays.OrderBy((DisplayFiles d) => d.ScreenIndex))
		{
			string fileName = Path.GetFileName(item.VideoPath);
			long length2 = new FileInfo(item.VideoPath).Length;
			bool flag = File.Exists(item.EventsPath);
			string text3 = (flag ? Path.GetFileName(item.EventsPath) : null);
			long? eventsSize = (flag ? new long?(new FileInfo(item.EventsPath).Length) : ((long?)null));
			SegmentDisplayManifest segmentDisplayManifest = FindDisplayManifest(segmentManifest, item.ScreenIndex, fileName);
			videoFiles.Add(new SegmentFile(fileName, length2));
			if (text3 != null)
			{
				supportFiles.Add(new SegmentFile(text3, eventsSize.Value));
			}
			string text4 = segmentDisplayManifest?.FrameSidecarFile;
			if (string.IsNullOrWhiteSpace(text4))
			{
				text4 = $"frame_timestamps_{item.ScreenIndex}_{seg.SegmentIndex}.jsonl";
			}
			AddSupportFileIfPresent(seg.SessionDir, text4, supportFiles);
			if (segmentDisplayManifest != null && !string.Equals(text4, segmentDisplayManifest.FrameSidecarFile, StringComparison.OrdinalIgnoreCase))
			{
				AddSupportFileIfPresent(seg.SessionDir, segmentDisplayManifest.FrameSidecarFile, supportFiles);
			}
			string path = $"metadata_{item.ScreenIndex}_{seg.SegmentIndex}.json";
			string metadataPath = Path.Combine(seg.SessionDir, path);
			if (!existing.Contains(item.ScreenIndex))
			{
				MetadataRoot value = BuildPerDisplayMetadata(seg.ClientSessionId, seg.SegmentIndex, item.ScreenIndex, fileName, length2, text3, eventsSize, durationMs, capturedAt, uploadedAt, osVersion, machineModel, scan, segmentDisplayManifest, BuildSegmentEndMetadata(scan, segmentManifest), sessionFacts, audioFile?.Name, audioFile?.SizeBytes, ((object)audio == null) ? null : audio with
				{
					FirstFrameOffsetUs = segmentDisplayManifest?.AudioFirstFrameOffsetUs
				});
				await WriteNewMetadataAsync(metadataPath, JsonSerializer.Serialize(value, JsonOptions)).ConfigureAwait(continueOnCapturedContext: false);
			}
			supportFiles.Add(new SegmentFile(path, new FileInfo(metadataPath).Length));
		}
		if ((object)audioFile != null)
		{
			videoFiles.Add(audioFile);
		}
		List<SegmentFile> list = new List<SegmentFile>(videoFiles.Count + supportFiles.Count);
		list.AddRange(videoFiles);
		list.AddRange(supportFiles);
		return new SegmentMetrics(durationMs, scan.IdleDurationMs, capturedAt, list);
	}

	private static async Task<FrozenSnapshot?> ReadFrozenSnapshotAsync(string path, SegmentInfo segment, DisplayFiles display, long durationMs, long idleMs)
	{
		_ = 1;
		try
		{
			FrozenSnapshot result;
			await using (FileStream stream = File.OpenRead(path))
			{
				long length = stream.Length;
				if (length > 4194304)
				{
					throw new InvalidDataException("Existing metadata exceeds its size limit");
				}
				byte[] bytes = new byte[(int)length + 1];
				int count;
				int num;
				for (count = 0; count < bytes.Length; count += num)
				{
					num = await stream.ReadAsync(bytes.AsMemory(count)).ConfigureAwait(continueOnCapturedContext: false);
					if (num == 0)
					{
						break;
					}
				}
				if (count > length)
				{
					throw new InvalidDataException("Existing metadata changed during validation");
				}
				using JsonDocument jsonDocument = JsonDocument.Parse(bytes.AsMemory(0, count));
				JsonElement rootElement = jsonDocument.RootElement;
				JsonElement property = rootElement.GetProperty("session");
				JsonElement property2 = rootElement.GetProperty("segment");
				if (property2.GetProperty("claimed_duration_ms").GetInt64() > 960000)
				{
					result = null;
				}
				else
				{
					JsonElement property3 = rootElement.GetProperty("files");
					DateTimeOffset dateTimeOffset = property.GetProperty("uploaded_at").GetDateTimeOffset();
					bool flag = File.Exists(display.EventsPath);
					if (property.GetProperty("client_session_id").GetString() != segment.ClientSessionId || property2.GetProperty("segment_index").GetInt32() != segment.SegmentIndex || property2.GetProperty("display_index").GetInt32() != display.ScreenIndex || property2.GetProperty("claimed_duration_ms").GetInt64() != durationMs || property2.GetProperty("idle_duration_ms").GetInt64() != idleMs || property3.GetProperty("clip_file").GetString() != Path.GetFileName(display.VideoPath) || property3.GetProperty("clip_size_bytes").GetInt64() != new FileInfo(display.VideoPath).Length || (flag && (property3.GetProperty("events_file").GetString() != Path.GetFileName(display.EventsPath) || property3.GetProperty("events_size_bytes").GetInt64() != new FileInfo(display.EventsPath).Length)) || (!flag && property3.TryGetProperty("events_file", out var value) && value.ValueKind != JsonValueKind.Null) || rootElement.GetProperty("device").GetProperty("platform").GetString() != "windows" || rootElement.GetProperty("activity").ValueKind != JsonValueKind.Object || rootElement.GetProperty("integrity").ValueKind != JsonValueKind.Object)
					{
						throw new InvalidDataException("Existing metadata does not match its segment artifacts");
					}
					result = new FrozenSnapshot(dateTimeOffset);
				}
			}
			return result;
		}
		catch (Exception ex) when ((ex is JsonException || ex is KeyNotFoundException || ex is InvalidOperationException || ex is FormatException) ? true : false)
		{
			throw new InvalidDataException("Existing metadata is invalid: " + Path.GetFileName(path), ex);
		}
	}

	private static async Task WriteNewMetadataAsync(string path, string json)
	{
		string temporary = $"{path}.{Guid.NewGuid():N}.tmp";
		try
		{
			await File.WriteAllTextAsync(temporary, json).ConfigureAwait(continueOnCapturedContext: false);
			File.Move(temporary, path, overwrite: false);
		}
		finally
		{
			if (File.Exists(temporary))
			{
				File.Delete(temporary);
			}
		}
	}

	private static SegmentDisplayManifest? FindDisplayManifest(SegmentManifest? segmentManifest, int displayIndex, string videoName)
	{
		if (segmentManifest == null)
		{
			return null;
		}
		return segmentManifest.Displays.FirstOrDefault((SegmentDisplayManifest d) => d.DisplayIndex == displayIndex) ?? segmentManifest.Displays.FirstOrDefault((SegmentDisplayManifest d) => string.Equals(d.VideoFile, videoName, StringComparison.OrdinalIgnoreCase));
	}

	private static void AddSupportFileIfPresent(string sessionDir, string? fileName, List<SegmentFile> supportFiles)
	{
		if (string.IsNullOrWhiteSpace(fileName) || supportFiles.Any((SegmentFile f) => string.Equals(f.Name, fileName, StringComparison.OrdinalIgnoreCase)))
		{
			return;
		}
		string text = Path.Combine(sessionDir, fileName);
		if (File.Exists(text))
		{
			long length = new FileInfo(text).Length;
			if (length > 0)
			{
				supportFiles.Add(new SegmentFile(fileName, length));
			}
		}
	}

	private static SegmentFile? AudioFileIfPresent(string sessionDir, SegmentManifest? manifest)
	{
		string text = manifest?.AudioFile;
		if (text == null)
		{
			return null;
		}
		string text2 = Path.Combine(sessionDir, text);
		if (!File.Exists(text2))
		{
			return null;
		}
		long length;
		try
		{
			length = new FileInfo(text2).Length;
			using (new FileStream(text2, FileMode.Open, FileAccess.Read, FileShare.Read))
			{
			}
		}
		catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
		{
			KuroLog.Upload($"audio file unreadable session={Path.GetFileName(sessionDir)} seg={manifest.SegmentIndex} file={text} error={ex.GetType().Name}", "AudioFileIfPresent", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Upload\\MetadataBuilder.cs", 336);
			return null;
		}
		if (length <= 0)
		{
			return null;
		}
		long? audioBytes = manifest.AudioBytes;
		if (audioBytes.HasValue)
		{
			long valueOrDefault = audioBytes.GetValueOrDefault();
			if (valueOrDefault != length)
			{
				KuroLog.Upload($"audio file size differs session={Path.GetFileName(sessionDir)} seg={manifest.SegmentIndex} file={text} manifest_bytes={valueOrDefault} disk_bytes={length}", "AudioFileIfPresent", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Upload\\MetadataBuilder.cs", 342);
			}
		}
		return new SegmentFile(text, length);
	}

	internal static AudioMetadata? BuildAudioMetadata(SegmentManifest? manifest)
	{
		SegmentAudioFacts segmentAudioFacts = manifest?.Audio;
		if (segmentAudioFacts != null)
		{
			AudioMetadata audioMetadata = new AudioMetadata
			{
				State = segmentAudioFacts.State,
				Reason = segmentAudioFacts.Reason,
				File = segmentAudioFacts.File,
				Source = segmentAudioFacts.Source,
				Codec = segmentAudioFacts.Codec,
				Encoder = segmentAudioFacts.Encoder,
				SampleRate = segmentAudioFacts.SampleRate,
				Channels = segmentAudioFacts.Channels,
				Bitrate = segmentAudioFacts.Bitrate,
				BandwidthHz = segmentAudioFacts.BandwidthHz,
				Frames = segmentAudioFacts.Frames,
				SoundMs = segmentAudioFacts.SoundMs,
				DelaySamples = segmentAudioFacts.DelaySamples,
				AnchorWallMs = segmentAudioFacts.AnchorWallMs,
				EndWallMs = segmentAudioFacts.EndWallMs,
				ZerosFilled = segmentAudioFacts.ZerosFilled,
				SamplesDropped = segmentAudioFacts.SamplesDropped,
				Corrections = segmentAudioFacts.Corrections,
				MaxAbsErrorUs = segmentAudioFacts.MaxAbsErrorUs,
				TimestampErrors = segmentAudioFacts.TimestampErrors,
				Discontinuities = segmentAudioFacts.Discontinuities,
				DeviceReopens = segmentAudioFacts.DeviceReopens,
				DroppedMs = segmentAudioFacts.DroppedMs,
				StarvedPeriods = segmentAudioFacts.StarvedPeriods
			};
			SegmentAudioMixFormat mixFormat = segmentAudioFacts.MixFormat;
			audioMetadata.MixFormat = ((mixFormat != null) ? new AudioMixFormatMetadata
			{
				Rate = mixFormat.Rate,
				Channels = mixFormat.Channels,
				Bits = mixFormat.Bits
			} : null);
			audioMetadata.Keepalive = segmentAudioFacts.Keepalive;
			audioMetadata.SealTailZeros = segmentAudioFacts.SealTailZeros;
			audioMetadata.SealOverrunSamples = segmentAudioFacts.SealOverrunSamples;
			audioMetadata.InterruptedAtMs = segmentAudioFacts.InterruptedAtMs;
			return audioMetadata;
		}
		return null;
	}

	private static async Task<long> GetClaimedDurationMsAsync(IReadOnlyList<DisplayFiles> displays)
	{
		long num = 0L;
		foreach (DisplayFiles display in displays)
		{
			long val = num;
			num = Math.Max(val, await GetMp4DurationMsAsync(display.VideoPath, display.CaptureFps).ConfigureAwait(continueOnCapturedContext: false));
		}
		return num;
	}

	private static long? GetClaimedDurationFromManifest(SegmentManifest? manifest)
	{
		long? num = manifest?.ClaimedDurationMs;
		if (num.HasValue && num.GetValueOrDefault() > 0)
		{
			return manifest.ClaimedDurationMs.Value;
		}
		return manifest?.Displays.Select((SegmentDisplayManifest d) =>
		{
			if (!d.FirstPtsNs.HasValue || !d.LastPtsNs.HasValue || d.LastPtsNs.Value < d.FirstPtsNs.Value)
			{
				return (long?)null;
			}
			int num2 = ((d.CaptureFps > 0) ? d.CaptureFps : 30);
			long? lastFrameDurationNs = d.LastFrameDurationNs;
			long num3 = ((lastFrameDurationNs.HasValue && lastFrameDurationNs.GetValueOrDefault() > 0) ? d.LastFrameDurationNs.Value : (1000000000L / (long)num2));
			return (d.LastPtsNs.Value - d.FirstPtsNs.Value + num3 + 999999) / 1000000;
		}).Where((long? v) => v.HasValue && v.GetValueOrDefault() > 0).DefaultIfEmpty()
			.Max();
	}

	private static MetadataRoot BuildPerDisplayMetadata(string clientSessionId, int segmentIndex, int displayIndex, string clipFile, long clipSize, string? eventsFile, long? eventsSize, long claimedDurationMs, DateTimeOffset? capturedAt, DateTimeOffset uploadedAt, string osVersion, string machineModel, EventScanResult scan, SegmentDisplayManifest? displayManifest, SegmentEndMetadata? segmentEnd, SessionFactsMetadata? sessionFacts, string? audioFile, long? audioSize, AudioMetadata? audio)
	{
		int processorCount = Environment.ProcessorCount;
		long totalPhysicalMemoryBytes = GetTotalPhysicalMemoryBytes();
		long physicalMemoryGb = ((totalPhysicalMemoryBytes > 0) ? (totalPhysicalMemoryBytes / 1073741824) : 0);
		string ianaId;
		return new MetadataRoot
		{
			Session = new SessionMetadata
			{
				ClientSessionId = clientSessionId,
				CapturedAt = capturedAt,
				UploadedAt = uploadedAt
			},
			Device = new DeviceMetadata
			{
				Platform = "windows",
				OsVersion = osVersion,
				MachineModel = machineModel,
				PhysicalMemoryGb = physicalMemoryGb,
				CpuCount = processorCount,
				Timezone = (TimeZoneInfo.TryConvertWindowsIdToIanaId(TimeZoneInfo.Local.Id, out ianaId) ? ianaId : TimeZoneInfo.Local.Id),
				Locale = CultureInfo.CurrentCulture.Name.Replace('-', '_')
			},
			Segment = new SegmentMetadata
			{
				SegmentIndex = segmentIndex,
				DisplayIndex = displayIndex,
				ClaimedDurationMs = claimedDurationMs,
				IdleDurationMs = scan.IdleDurationMs,
				DurationSeconds = (double)claimedDurationMs / 1000.0
			},
			Activity = new ActivityMetadata
			{
				TotalClicks = scan.Clicks,
				TotalKeystrokes = scan.Keystrokes,
				TotalScrolls = scan.Scrolls,
				AppUsage = ((scan.AppUsage.Count > 0) ? scan.AppUsage : null)
			},
			Files = new FilesMetadata
			{
				ClipFile = clipFile,
				ClipSizeBytes = clipSize,
				EventsFile = eventsFile,
				EventsSizeBytes = eventsSize,
				AudioFile = audioFile,
				AudioSizeBytes = audioSize
			},
			Integrity = new IntegrityMetadata
			{
				SyntheticEventCount = scan.SyntheticEventCount,
				TotalInputEventCount = scan.TotalInputEventCount,
				TimingCv = scan.TimingCv,
				JigglerDetected = scan.JigglerDetected,
				JigglerAppName = scan.JigglerAppName,
				TimingValidation = BuildTimingValidationMetadata(displayManifest),
				CaptureFreshness = BuildCaptureFreshnessMetadata(displayManifest)
			},
			SegmentEnd = segmentEnd,
			SessionFacts = sessionFacts,
			Audio = audio
		};
	}

	private static TimingValidationMetadata? BuildTimingValidationMetadata(SegmentDisplayManifest? display)
	{
		if (display == null || !display.TimingSuspect)
		{
			return null;
		}
		string suspectReason = display.TimingSuspectReason ?? "client_timing_suspect";
		return new TimingValidationMetadata
		{
			Suspect = true,
			SuspectReason = suspectReason,
			SidecarIntegrityError = display.SidecarIntegrityError,
			EncodedFrameCount = ((display.EncodedFrameCount > 0) ? new long?(display.EncodedFrameCount) : ((long?)null)),
			CaptureFps = ((display.CaptureFps > 0) ? new int?(display.CaptureFps) : ((int?)null)),
			BitrateBps = ((display.BitrateBps != 0) ? new uint?(display.BitrateBps) : ((uint?)null)),
			FallbackMode = display.FallbackMode
		};
	}

	private static CaptureFreshnessMetadata? BuildCaptureFreshnessMetadata(SegmentDisplayManifest? display)
	{
		if (display == null || !display.CaptureSurfaceStale)
		{
			return null;
		}
		return new CaptureFreshnessMetadata
		{
			Suspect = true,
			SuspectReason = display.CaptureSurfaceStaleReason,
			EncodedFrameCount = ((display.EncodedFrameCount > 0) ? new long?(display.EncodedFrameCount) : ((long?)null)),
			SourceFrameCount = ((display.SourceFrameCount >= 0) ? new long?(display.SourceFrameCount) : ((long?)null)),
			CursorOnlyFrameCount = ((display.CursorOnlyFrameCount >= 0) ? new long?(display.CursorOnlyFrameCount) : ((long?)null)),
			DuplicateFrameCount = ((display.DuplicateFrameCount >= 0) ? new long?(display.DuplicateFrameCount) : ((long?)null)),
			SourceFrameDurationMs = ((display.SourceFrameDurationNs > 0) ? new long?(display.SourceFrameDurationNs / 1000000) : ((long?)null)),
			CursorOnlyFrameDurationMs = ((display.CursorOnlyFrameDurationNs > 0) ? new long?(display.CursorOnlyFrameDurationNs / 1000000) : ((long?)null)),
			DuplicateFrameDurationMs = ((display.DuplicateFrameDurationNs > 0) ? new long?(display.DuplicateFrameDurationNs / 1000000) : ((long?)null)),
			ActiveInputEventCount = ((display.ActiveInputEventCount >= 0) ? new long?(display.ActiveInputEventCount) : ((long?)null)),
			UniqueSourceFrameHashCount = ((display.UniqueSourceFrameHashCount >= 0) ? new long?(display.UniqueSourceFrameHashCount) : ((long?)null)),
			SourceFrameVisualChangeScore = ((display.SourceFrameVisualChangeScore >= 0) ? new long?(display.SourceFrameVisualChangeScore) : ((long?)null)),
			CaptureFps = ((display.CaptureFps > 0) ? new int?(display.CaptureFps) : ((int?)null)),
			BitrateBps = ((display.BitrateBps != 0) ? new uint?(display.BitrateBps) : ((uint?)null)),
			FallbackMode = display.FallbackMode
		};
	}

	internal static SessionFactsMetadata? BuildSessionFactsMetadata(SegmentManifest? manifest)
	{
		SegmentSessionFacts segmentSessionFacts = manifest?.SessionFacts;
		if (segmentSessionFacts == null)
		{
			return null;
		}
		SessionFactsMetadata sessionFactsMetadata = new SessionFactsMetadata
		{
			V = segmentSessionFacts.V
		};
		object obj;
		if (segmentSessionFacts.Input != null)
		{
			obj = new SessionFactsInputMetadata
			{
				Events = segmentSessionFacts.Input.Events,
				Injected = segmentSessionFacts.Input.Injected,
				InjectedMouse = segmentSessionFacts.Input.InjectedMouse,
				InjectedKey = segmentSessionFacts.Input.InjectedKey
			};
			object sessionFactsInputMetadata = obj;
			List<SegmentInputExtraInfo> extraInfo = segmentSessionFacts.Input.ExtraInfo;
			((SessionFactsInputMetadata)sessionFactsInputMetadata).ExtraInfo = ((extraInfo != null && extraInfo.Count > 0) ? (from e in extraInfo.Where((SegmentInputExtraInfo e) => !string.IsNullOrEmpty(e.Tag)).Take(4)
				select new SessionFactsExtraInfoMetadata
				{
					Tag = (HardwareFacets.Text(e.Tag, 16) ?? ""),
					N = e.N
				}).ToList() : null);
			((SessionFactsInputMetadata)obj).RawPackets = segmentSessionFacts.Input.RawPackets;
			((SessionFactsInputMetadata)obj).RawNullDevice = segmentSessionFacts.Input.RawNullDevice;
			((SessionFactsInputMetadata)obj).RawAbsolute = segmentSessionFacts.Input.RawAbsolute;
			object sessionFactsInputMetadata2 = obj;
			List<SegmentInputDevice> devices = segmentSessionFacts.Input.Devices;
			((SessionFactsInputMetadata)sessionFactsInputMetadata2).Devices = ((devices != null && devices.Count > 0) ? devices.Take(6).Select((SegmentInputDevice d) =>
			{
				SessionFactsDeviceMetadata sessionFactsDeviceMetadata = new SessionFactsDeviceMetadata
				{
					Vid = HardwareFacets.Text(d.Vid, 4),
					Pid = HardwareFacets.Text(d.Pid, 4)
				};
				SessionFactsDeviceMetadata sessionFactsDeviceMetadata2 = sessionFactsDeviceMetadata;
				string kind = d.Kind;
				bool flag = ((kind == "mouse" || kind == "keyboard") ? true : false);
				sessionFactsDeviceMetadata2.Kind = (flag ? d.Kind : "hid");
				sessionFactsDeviceMetadata.Name = HardwareFacets.Text(d.Name, 48);
				return sessionFactsDeviceMetadata;
			}).ToList() : null);
			((SessionFactsInputMetadata)obj).RawKeyboard = segmentSessionFacts.Input.RawKeyboard;
			((SessionFactsInputMetadata)obj).RawFailures = segmentSessionFacts.Input.RawFailures;
			((SessionFactsInputMetadata)obj).GamepadPackets = segmentSessionFacts.Input.GamepadPackets;
			((SessionFactsInputMetadata)obj).Rearms = segmentSessionFacts.Input.Rearms;
			((SessionFactsInputMetadata)obj).GapMs = segmentSessionFacts.Input.GapMs;
			((SessionFactsInputMetadata)obj).BlindMs = segmentSessionFacts.Input.BlindMs;
			((SessionFactsInputMetadata)obj).Dropped = segmentSessionFacts.Input.Dropped;
			((SessionFactsInputMetadata)obj).Late = segmentSessionFacts.Input.Late;
			((SessionFactsInputMetadata)obj).Thinned = segmentSessionFacts.Input.Thinned;
			((SessionFactsInputMetadata)obj).QueuePeak = segmentSessionFacts.Input.QueuePeak;
			((SessionFactsInputMetadata)obj).Witness = segmentSessionFacts.Input.Witness;
			object sessionFactsInputMetadata3 = obj;
			SegmentTouchpadFacts touchpad = segmentSessionFacts.Input.Touchpad;
			((SessionFactsInputMetadata)sessionFactsInputMetadata3).Touchpad = ((touchpad != null) ? new SessionFactsTouchpadMetadata
			{
				Devices = touchpad.Devices,
				Frames = touchpad.Frames,
				Reports = touchpad.Reports,
				Partial = touchpad.Partial,
				Timeouts = touchpad.Timeouts,
				DupIds = touchpad.DupIds,
				UnknownReportId = touchpad.UnknownReportId,
				UnknownDevice = touchpad.UnknownDevice,
				LenMismatch = touchpad.LenMismatch,
				RawFail = touchpad.RawFail,
				BufferGrow = touchpad.BufferGrow,
				ContactsTruncated = touchpad.ContactsTruncated,
				Gestures = new SessionFactsTouchpadGestureMetadata
				{
					Scroll = touchpad.Gestures.Scroll,
					Pinch = touchpad.Gestures.Pinch,
					Swipe = touchpad.Gestures.Swipe,
					Pan = touchpad.Gestures.Pan,
					Other = touchpad.Gestures.Other
				},
				ScrollRows = touchpad.ScrollRows,
				OsRows = touchpad.OsRows,
				OrphanWheels = touchpad.OrphanWheels,
				PalmContacts = touchpad.PalmContacts,
				Registered = touchpad.Registered,
				RegisterFail = touchpad.RegisterFail,
				Reregisters = touchpad.Reregisters
			} : null);
		}
		else
		{
			obj = null;
		}
		sessionFactsMetadata.Input = (SessionFactsInputMetadata?)obj;
		sessionFactsMetadata.Session = ((segmentSessionFacts.Session == null) ? null : new SessionFactsSessionMetadata
		{
			Remote = segmentSessionFacts.Session.Remote,
			ConnectState = HardwareFacets.Text(segmentSessionFacts.Session.ConnectState, 20),
			RemoteTools = ((segmentSessionFacts.Session.RemoteTools == null) ? null : (from t in (from t in segmentSessionFacts.Session.RemoteTools
					select HardwareFacets.Text(t, 32) into t
					where t != null
					select t).Take(8)
				select (t)).ToList())
		});
		List<SegmentDisplayFact> displays = segmentSessionFacts.Displays;
		sessionFactsMetadata.Displays = ((displays != null && displays.Count > 0) ? (from d in displays.Take(4)
			select new SessionFactsDisplayMetadata
			{
				Tech = HardwareFacets.Text(d.Tech, 24),
				Mfr = HardwareFacets.Text(d.Mfr, 3),
				Pid = HardwareFacets.Text(d.Pid, 4),
				Serial = d.Serial,
				Adapter = HardwareFacets.Text(d.Adapter, 48),
				Builtin = d.Builtin
			}).ToList() : null);
		SessionFactsMetadata sessionFactsMetadata2 = sessionFactsMetadata;
		while (CompactBytes(sessionFactsMetadata2) > 2048)
		{
			List<SessionFactsDeviceMetadata> list = sessionFactsMetadata2.Input?.Devices;
			if (list != null && list.Count > 0)
			{
				list.RemoveAt(list.Count - 1);
				if (list.Count == 0)
				{
					sessionFactsMetadata2.Input.Devices = null;
				}
				continue;
			}
			if (sessionFactsMetadata2.Input?.ExtraInfo != null)
			{
				sessionFactsMetadata2.Input.ExtraInfo = null;
				continue;
			}
			if (sessionFactsMetadata2.Input?.Touchpad != null)
			{
				sessionFactsMetadata2.Input.Touchpad = null;
				continue;
			}
			List<SessionFactsDisplayMetadata> displays2 = sessionFactsMetadata2.Displays;
			if (displays2 != null && displays2.Count > 0)
			{
				displays2.RemoveAt(displays2.Count - 1);
				if (displays2.Count == 0)
				{
					sessionFactsMetadata2.Displays = null;
				}
				continue;
			}
			List<string> list2 = sessionFactsMetadata2.Session?.RemoteTools;
			if (list2 != null && list2.Count > 0)
			{
				list2.RemoveAt(list2.Count - 1);
				if (list2.Count == 0)
				{
					sessionFactsMetadata2.Session.RemoteTools = null;
				}
				continue;
			}
			return null;
		}
		return sessionFactsMetadata2;
	}

	private static int CompactBytes(SessionFactsMetadata block)
	{
		return Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(block, CompactJsonOptions));
	}

	private static SegmentEndMetadata? BuildSegmentEndMetadata(EventScanResult scan, SegmentManifest? manifest)
	{
		if (scan.SegmentEndReason != null)
		{
			return new SegmentEndMetadata
			{
				Reason = scan.SegmentEndReason,
				MaskTrigger = ((scan.SegmentEndReason == "privacy_mask" && scan.SegmentMaskTrigger != null) ? new MaskTriggerMetadata
				{
					Type = scan.SegmentMaskTrigger,
					Detail = scan.SegmentMaskDetail
				} : null)
			};
		}
		if (manifest?.EndReason == null)
		{
			return null;
		}
		return new SegmentEndMetadata
		{
			Reason = manifest.EndReason,
			MaskTrigger = ((manifest.EndReason == "privacy_mask" && manifest.MaskTrigger != null) ? new MaskTriggerMetadata
			{
				Type = manifest.MaskTrigger,
				Detail = manifest.MaskDetail
			} : null)
		};
	}

	internal static EventScanResult ScanEvents(string eventsPath, long claimedDurationMs)
	{
		try
		{
			double num = 5000.0;
			double? num2 = null;
			List<double> list = new List<double>();
			Dictionary<string, double> dictionary = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
			(string, double)? tuple = null;
			int num3 = 0;
			int num4 = 0;
			int num5 = 0;
			int num6 = 0;
			int num7 = 0;
			bool flag = false;
			string jigglerAppName = null;
			string segmentEndReason = null;
			string segmentMaskTrigger = null;
			string segmentMaskDetail = null;
			foreach (string item2 in File.ReadLines(eventsPath))
			{
				if (string.IsNullOrWhiteSpace(item2) || item2.StartsWith("{\"type\":\"touch\",", StringComparison.Ordinal))
				{
					continue;
				}
				try
				{
					using JsonDocument jsonDocument = JsonDocument.Parse(item2);
					JsonElement rootElement = jsonDocument.RootElement;
					if (!TryGetString(rootElement, "type", out string value) || !TryGetDouble(rootElement, "timestamp", out var value2))
					{
						continue;
					}
					if (!PreambleTypes.Contains(value))
					{
						double valueOrDefault = num2.GetValueOrDefault();
						if (!num2.HasValue)
						{
							valueOrDefault = value2;
							num2 = valueOrDefault;
						}
					}
					bool flag2 = ActivityTypes.Contains(value) || value == "mouse_move";
					if (flag2)
					{
						num7++;
					}
					if (((TryGetInt(rootElement, "sourcePid") ?? TryGetInt(rootElement, "source_pid").GetValueOrDefault()) != 0) & flag2)
					{
						num6++;
						continue;
					}
					if ((value == "app_launch" || value == "active_app") ? true : false)
					{
						string item = TryGetString(rootElement, "bundleId") ?? TryGetString(rootElement, "bundle_id") ?? "";
						string text = TryGetString(rootElement, "name") ?? "";
						if (JigglerBundleIds.Contains(item) || JigglerExactNames.Contains(text.ToLowerInvariant()))
						{
							flag = true;
							jigglerAppName = text;
						}
					}
					if (ActivityTypes.Contains(value))
					{
						list.Add(value2);
					}
					switch (value)
					{
					case "click":
						num3++;
						break;
					case "key":
						num4++;
						break;
					case "scroll":
						num5++;
						break;
					case "active_app":
					{
						if (TryGetString(rootElement, "name", out string value3))
						{
							if (tuple.HasValue)
							{
								(string, double) valueOrDefault2 = tuple.GetValueOrDefault();
								dictionary[valueOrDefault2.Item1] = dictionary.GetValueOrDefault(valueOrDefault2.Item1) + Math.Max(0.0, value2 - valueOrDefault2.Item2);
							}
							tuple = (value3, value2);
						}
						break;
					}
					case "segment_end":
						segmentEndReason = TryGetString(rootElement, "reason");
						segmentMaskTrigger = TryGetString(rootElement, "maskTrigger") ?? TryGetString(rootElement, "mask_trigger");
						segmentMaskDetail = TryGetString(rootElement, "maskDetail") ?? TryGetString(rootElement, "mask_detail");
						break;
					}
				}
				catch
				{
				}
			}
			if (tuple.HasValue)
			{
				(string, double) valueOrDefault3 = tuple.GetValueOrDefault();
				if (num2.HasValue)
				{
					dictionary[valueOrDefault3.Item1] = dictionary.GetValueOrDefault(valueOrDefault3.Item1) + Math.Max(0.0, num2.Value + (double)claimedDurationMs - valueOrDefault3.Item2);
				}
			}
			double totalAppTime = Math.Max(dictionary.Values.Sum(), 1.0);
			Dictionary<string, double> appUsage = dictionary.ToDictionary((KeyValuePair<string, double> kvp) => kvp.Key, (KeyValuePair<string, double> kvp) => Math.Round(kvp.Value / totalAppTime, 2), StringComparer.OrdinalIgnoreCase);
			double? timingCv = ComputeTimingCv(list);
			if (flag)
			{
				list.Clear();
			}
			long idleDurationMs;
			if (!num2.HasValue)
			{
				idleDurationMs = claimedDurationMs;
			}
			else if (list.Count == 0)
			{
				idleDurationMs = claimedDurationMs;
			}
			else
			{
				double num8 = num2.Value + (double)claimedDurationMs;
				double num9 = 0.0;
				double num10 = list[0] - num2.Value;
				if (num10 > num)
				{
					num9 += num10 - num;
				}
				for (int num11 = 1; num11 < list.Count; num11++)
				{
					double num12 = list[num11] - list[num11 - 1];
					if (num12 > num)
					{
						num9 += num12 - num;
					}
				}
				double num13 = num8 - list[list.Count - 1];
				if (num13 > num)
				{
					num9 += num13 - num;
				}
				idleDurationMs = Math.Min((long)num9, claimedDurationMs);
			}
			return new EventScanResult(idleDurationMs, num3, num4, num5, appUsage, segmentEndReason, segmentMaskTrigger, segmentMaskDetail, num6, num7, timingCv, flag, jigglerAppName);
		}
		catch (Exception ex)
		{
			KuroLog.Upload($"Event scan failed for {Path.GetFileName(eventsPath)}; treating full segment duration as idle: {ex.GetType().Name}: {ex.Message}", "ScanEvents", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Upload\\MetadataBuilder.cs", 956);
			return EventScanResult.Empty with
			{
				IdleDurationMs = claimedDurationMs
			};
		}
	}

	private static double? ComputeTimingCv(IReadOnlyList<double> timestamps)
	{
		if (timestamps.Count < 3)
		{
			return null;
		}
		double num = 0.0;
		double num2 = 0.0;
		double num3 = 0.0;
		for (int i = 1; i < timestamps.Count; i++)
		{
			double num4 = timestamps[i] - timestamps[i - 1];
			num++;
			double num5 = num4 - num2;
			num2 += num5 / num;
			double num6 = num4 - num2;
			num3 += num5 * num6;
		}
		if (num < 2.0 || num2 <= 0.0)
		{
			return null;
		}
		return Math.Round(Math.Sqrt(num3 / (num - 1.0)) / num2, 3);
	}

	private static bool TryGetString(JsonElement element, string propertyName, out string value)
	{
		value = "";
		if (!element.TryGetProperty(propertyName, out var value2))
		{
			return false;
		}
		if (value2.ValueKind == JsonValueKind.String)
		{
			value = value2.GetString() ?? "";
			return true;
		}
		return false;
	}

	private static string? TryGetString(JsonElement element, string propertyName)
	{
		if (!TryGetString(element, propertyName, out string value))
		{
			return null;
		}
		return value;
	}

	private static bool TryGetDouble(JsonElement element, string propertyName, out double value)
	{
		value = 0.0;
		if (element.TryGetProperty(propertyName, out var value2))
		{
			return value2.TryGetDouble(out value);
		}
		return false;
	}

	private static int? TryGetInt(JsonElement element, string propertyName)
	{
		if (element.TryGetProperty(propertyName, out var value) && value.TryGetInt32(out var value2))
		{
			return value2;
		}
		return null;
	}

	private static async Task<long> GetMp4DurationMsAsync(string path, int captureFps)
	{
		_ = 1;
		try
		{
			long num = (long)(await (await StorageFile.GetFileFromPathAsync(path)).Properties.GetVideoPropertiesAsync()).Duration.TotalMilliseconds;
			if (num > 0)
			{
				return num;
			}
		}
		catch
		{
		}
		return GetDurationFromFrameSidecar(path, captureFps);
	}

	private static long GetDurationFromFrameSidecar(string videoPath, int captureFps)
	{
		try
		{
			string[] array = Path.GetFileNameWithoutExtension(videoPath).Split('_');
			if (array.Length != 3 || !int.TryParse(array[1], out var result) || !int.TryParse(array[2], out var result2))
			{
				return 0L;
			}
			string path = Path.Combine(Path.GetDirectoryName(videoPath), $"frame_timestamps_{result}_{result2}.jsonl");
			if (!File.Exists(path))
			{
				return 0L;
			}
			long? num = null;
			long? num2 = null;
			long? num3 = null;
			long num4 = 0L;
			foreach (string item in File.ReadLines(path))
			{
				if (string.IsNullOrWhiteSpace(item))
				{
					continue;
				}
				using JsonDocument jsonDocument = JsonDocument.Parse(item);
				if (jsonDocument.RootElement.TryGetProperty("pts_ns", out var value) && value.TryGetInt64(out var value2))
				{
					long valueOrDefault = num.GetValueOrDefault();
					if (!num.HasValue)
					{
						valueOrDefault = value2;
						num = valueOrDefault;
					}
					num2 = value2;
					if (jsonDocument.RootElement.TryGetProperty("duration_ns", out var value3) && value3.TryGetInt64(out var value4) && value4 > 0)
					{
						num3 = value4;
					}
					num4++;
				}
			}
			if (num.HasValue && num2.HasValue && num2.Value >= num.Value)
			{
				long num5 = ((num3.HasValue && num3.GetValueOrDefault() > 0) ? num3.Value : (1000000000L / (long)Math.Max(captureFps, 1)));
				return Math.Max(1L, (num2.Value - num.Value + num5 + 999999) / 1000000);
			}
			return (num4 > 0) ? Math.Max(1L, num4 * 1000 / Math.Max(captureFps, 1)) : 0;
		}
		catch
		{
			return 0L;
		}
	}

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);

	private static long GetTotalPhysicalMemoryBytes()
	{
		if (!OperatingSystem.IsWindows())
		{
			return 0L;
		}
		MEMORYSTATUSEX buffer = new MEMORYSTATUSEX
		{
			dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>()
		};
		if (!GlobalMemoryStatusEx(ref buffer))
		{
			return 0L;
		}
		return (long)buffer.ullTotalPhys;
	}

	private static DateTimeOffset? ParseCapturedAt(string sessionId)
	{
		if (DateTime.TryParseExact(sessionId, "yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var result))
		{
			return new DateTimeOffset(result).ToUniversalTime();
		}
		return null;
	}
}
