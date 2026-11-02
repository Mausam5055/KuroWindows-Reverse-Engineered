using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using ClientTelemetry;
using KuroWindows.Capture;
using KuroWindows.RecorderCore.Capture;
using KuroWindows.RecorderCore.Segments;

namespace KuroWindows.Storage;

public static class InterruptedSessionRecovery
{
	public static void Recover(StorageManager storage)
	{
		string[] sessionDirs;
		try
		{
			sessionDirs = storage.EnumerateSessionDirectories().ToArray();
		}
		catch (Exception ex)
		{
			Log("Recovery candidate scan failed for the recordings root: " + ex.Message, "Recover", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Storage\\InterruptedSessionRecovery.cs", 24);
			return;
		}
		Recover(storage, sessionDirs);
	}

	internal static void Recover(StorageManager storage, IEnumerable<string> sessionDirs)
	{
		foreach (StorageManager.InterruptedSessionInfo item in EnumerateRecoveryCandidates(sessionDirs))
		{
			RecoverSession(storage, item);
		}
	}

	private static IEnumerable<StorageManager.InterruptedSessionInfo> EnumerateRecoveryCandidates(IEnumerable<string> sessionDirs)
	{
		Dictionary<string, StorageManager.InterruptedSessionInfo> dictionary = new Dictionary<string, StorageManager.InterruptedSessionInfo>(StringComparer.OrdinalIgnoreCase);
		foreach (string sessionDir in sessionDirs)
		{
			string fileName = Path.GetFileName(sessionDir);
			try
			{
				StorageManager.InterruptedSessionInfo? interruptedSessionInfo = StorageManager.TryReadActiveSessionMarker(sessionDir);
				if (interruptedSessionInfo.HasValue)
				{
					StorageManager.InterruptedSessionInfo valueOrDefault = interruptedSessionInfo.GetValueOrDefault();
					dictionary[$"{sessionDir}:{valueOrDefault.SegmentIndex}"] = valueOrDefault;
				}
				foreach (string item in Directory.EnumerateFiles(sessionDir))
				{
					if (!SegmentManifestStore.TryParseManifestSegmentIndex(Path.GetFileName(item), out var segmentIndex))
					{
						continue;
					}
					string key = $"{sessionDir}:{segmentIndex}";
					if (dictionary.ContainsKey(key))
					{
						continue;
					}
					SegmentManifestLoadResult segmentManifestLoadResult = SegmentManifestStore.TryLoadDetailed(item);
					Exception readError = segmentManifestLoadResult.ReadError;
					if (readError != null)
					{
						KuroLog.LocalOnly("recording", $"recovery skipped session={fileName} seg={segmentIndex} manifest=unreadable error={KuroLog.ExceptionSummary(readError)}", "EnumerateRecoveryCandidates", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Storage\\InterruptedSessionRecovery.cs", 89);
					}
					else
					{
						SegmentManifest manifest = segmentManifestLoadResult.Manifest;
						if (manifest != null && SegmentManifestStore.IsInterruptedState(manifest.State))
						{
							dictionary.Add(key, new StorageManager.InterruptedSessionInfo(sessionDir, segmentIndex, new DateTimeOffset(segmentManifestLoadResult.LastWriteUtc, TimeSpan.Zero)));
						}
					}
				}
			}
			catch (Exception ex)
			{
				Log("Recovery candidate scan failed for " + fileName + ": " + ex.Message, "EnumerateRecoveryCandidates", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Storage\\InterruptedSessionRecovery.cs", 108);
			}
		}
		return dictionary.Values.OrderBy((StorageManager.InterruptedSessionInfo session) => session.SessionDir, StringComparer.OrdinalIgnoreCase).ThenBy((StorageManager.InterruptedSessionInfo session) => session.SegmentIndex).ToArray();
	}

	internal static void RecoverSession(StorageManager storage, StorageManager.InterruptedSessionInfo session)
	{
		SegmentManifestLoadResult segmentManifestLoadResult = SegmentManifestStore.TryLoadDetailed(SegmentManifestStore.ManifestPath(session.SessionDir, session.SegmentIndex));
		SegmentManifest manifest = segmentManifestLoadResult.Manifest;
		SegmentManifest manifest2 = manifest ?? new SegmentManifest
		{
			State = SegmentManifestState.Finalizing,
			SessionId = Path.GetFileName(session.SessionDir),
			SegmentIndex = session.SegmentIndex,
			StartedWallMs = session.UpdatedAtUtc.ToUnixTimeMilliseconds()
		};
		try
		{
			Exception readError = segmentManifestLoadResult.ReadError;
			if (readError != null)
			{
				KuroLog.LocalOnly("recording", $"recovery skipped session={Path.GetFileName(session.SessionDir)} seg={session.SegmentIndex} manifest=unreadable error={KuroLog.ExceptionSummary(readError)}", "RecoverSession", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Storage\\InterruptedSessionRecovery.cs", 138);
			}
			else if (manifest != null && !SegmentManifestStore.IsInterruptedState(manifest.State))
			{
				KuroLog.LocalOnly("recording", $"recovery skipped session={Path.GetFileName(session.SessionDir)} seg={session.SegmentIndex} state={JsonNamingPolicy.SnakeCaseLower.ConvertName(manifest.State.ToString())}", "RecoverSession", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Storage\\InterruptedSessionRecovery.cs", 150);
			}
			else
			{
				RecoverSegment(session, manifest2, manifest != null);
			}
		}
		catch (Exception ex)
		{
			SegmentFate.Quarantine(session.SessionDir, session.SegmentIndex, manifest2, QuarantineReason.RecoveryFailed, FateOrigin.LaunchRecovery, KuroLog.ExceptionSummary(ex));
		}
		finally
		{
			try
			{
				storage.ClearSessionActive(session.SessionDir);
			}
			catch (Exception ex2)
			{
				KuroLog.LocalOnly("recording", $"session marker not cleared session={Path.GetFileName(session.SessionDir)} seg={session.SegmentIndex} error={KuroLog.ExceptionSummary(ex2)}", "RecoverSession", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Storage\\InterruptedSessionRecovery.cs", 179);
			}
		}
	}

	private static void DropUnfinalizedAudio(string sessionDir, int segmentIndex, SegmentManifest manifest)
	{
		manifest.AudioFile = null;
		manifest.AudioBytes = null;
		manifest.AudioSha256 = null;
		string fileName = Path.GetFileName(sessionDir);
		bool flag = false;
		try
		{
			foreach (string item in Directory.EnumerateFiles(sessionDir))
			{
				string fileName2 = Path.GetFileName(item);
				if (SegmentManifestStore.IsAudioFile(fileName2, out var segmentIndex2) && segmentIndex2 == segmentIndex)
				{
					flag = true;
					try
					{
						File.Delete(item);
						KuroLog.LocalOnly("recording", $"audio file dropped session={fileName} seg={segmentIndex} file={fileName2}", "DropUnfinalizedAudio", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Storage\\InterruptedSessionRecovery.cs", 207);
					}
					catch (Exception ex)
					{
						KuroLog.LocalOnly("recording", $"audio file drop failed session={fileName} seg={segmentIndex} file={fileName2} error={KuroLog.ExceptionSummary(ex)}", "DropUnfinalizedAudio", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Storage\\InterruptedSessionRecovery.cs", 211);
					}
				}
			}
		}
		catch (Exception ex2)
		{
			KuroLog.LocalOnly("recording", $"audio file scan failed session={fileName} seg={segmentIndex} error={KuroLog.ExceptionSummary(ex2)}", "DropUnfinalizedAudio", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Storage\\InterruptedSessionRecovery.cs", 217);
		}
		if (flag)
		{
			manifest.Audio = new SegmentAudioFacts
			{
				State = "interrupted",
				Reason = "recovered_unfinalized"
			};
		}
	}

	private static void RecoverSegment(StorageManager.InterruptedSessionInfo session, SegmentManifest manifest, bool manifestOnDisk)
	{
		string fileName = Path.GetFileName(session.SessionDir);
		int num = 0;
		(QuarantineReason, string)? tuple = null;
		string text = null;
		List<long> list = new List<long>();
		SortedSet<int> sortedSet = new SortedSet<int>(manifest.Displays.Select((SegmentDisplayManifest d) => d.DisplayIndex));
		foreach (string item in Directory.EnumerateFiles(session.SessionDir))
		{
			if (SegmentManifestStore.TryParseClipDisplayIndex(Path.GetFileName(item), out var displayIndex, out var segmentIndex) && segmentIndex == session.SegmentIndex)
			{
				sortedSet.Add(displayIndex);
			}
		}
		List<string> list2 = LockedPayloadFiles(session, manifest, sortedSet);
		if ((list2.Count > 0) & manifestOnDisk)
		{
			KuroLog.LocalOnly("recording", $"recovery skipped session={fileName} seg={session.SegmentIndex} payload=locked file={list2[0]}", "RecoverSegment", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Storage\\InterruptedSessionRecovery.cs", 264);
			return;
		}
		foreach (int item2 in sortedSet)
		{
			string text2 = VideoFileName(manifest, item2, session.SegmentIndex);
			string videoPath = Path.Combine(session.SessionDir, text2);
			if (CommittedNoFrame(session.SessionDir, session.SegmentIndex, item2))
			{
				(QuarantineReason, string) valueOrDefault = tuple.GetValueOrDefault();
				if (!tuple.HasValue)
				{
					valueOrDefault = (QuarantineReason.ZeroFrames, $"display {item2}");
					tuple = valueOrDefault;
				}
				continue;
			}
			string text3 = $"events_{item2}_{session.SegmentIndex}.json";
			string text4 = Path.Combine(session.SessionDir, text3);
			if (!File.Exists(text4))
			{
				(QuarantineReason, string) valueOrDefault = tuple.GetValueOrDefault();
				if (!tuple.HasValue)
				{
					valueOrDefault = (QuarantineReason.FileMissing, text3);
					tuple = valueOrDefault;
				}
				continue;
			}
			if (list2.Contains(text2) || !IsRecoverable(videoPath))
			{
				(QuarantineReason, string) valueOrDefault = tuple.GetValueOrDefault();
				if (!tuple.HasValue)
				{
					valueOrDefault = (QuarantineReason.FileMissing, text2);
					tuple = valueOrDefault;
				}
				continue;
			}
			if (list2.Contains(text3) || !SegmentManifestStore.HasNonEmptyFile(session.SessionDir, text3))
			{
				(QuarantineReason, string) valueOrDefault = tuple.GetValueOrDefault();
				if (!tuple.HasValue)
				{
					valueOrDefault = (QuarantineReason.FileMissing, text3);
					tuple = valueOrDefault;
				}
				continue;
			}
			if (!CanResolveRecoveredSegmentEndTimestamp(session.SessionDir, session.SegmentIndex, item2, text4, out var timestampMs))
			{
				(QuarantineReason, string) valueOrDefault = tuple.GetValueOrDefault();
				if (!tuple.HasValue)
				{
					valueOrDefault = (QuarantineReason.TimestampUnresolved, $"display {item2}");
					tuple = valueOrDefault;
				}
				continue;
			}
			if (TryReadEventsTerminatorReason(text4, out string reason))
			{
				if (item2 == 0 && reason != null)
				{
					text = reason;
				}
			}
			else
			{
				AppendSegmentEnd(text4, session.SegmentIndex, item2, timestampMs);
			}
			UpdateDisplayManifest(manifest, session.SegmentIndex, item2, videoPath, text4);
			list.Add(timestampMs);
			num++;
		}
		DropUnfinalizedAudio(session.SessionDir, session.SegmentIndex, manifest);
		if (num == 0)
		{
			var (reason2, detail) = tuple ?? (QuarantineReason.NoDisplays, null);
			SegmentFate.Quarantine(session.SessionDir, session.SegmentIndex, manifest, reason2, FateOrigin.LaunchRecovery, detail);
			return;
		}
		manifest.EndReason = text ?? "capture_failure";
		manifest.EndSource = "interrupted_session_recovery";
		manifest.EndedWallMs = list.Max();
		SegmentAudioFacts audio = manifest.Audio;
		if (audio != null)
		{
			audio.InterruptedAtMs = manifest.EndedWallMs;
		}
		long? num2 = (from v in manifest.Displays.Select(GetEncodedDurationMs)
			where v.HasValue && v.GetValueOrDefault() > 0
			select v).DefaultIfEmpty().Max();
		long? num3 = TryGetRecoveredEventDurationMs(session.SessionDir, manifest);
		if (num2.HasValue && num2.GetValueOrDefault() > 0)
		{
			manifest.ClaimedDurationMs = num2.Value;
		}
		else if (num3.HasValue && num3.GetValueOrDefault() > 0)
		{
			manifest.ClaimedDurationMs = num3.Value;
		}
		else if (manifest.StartedWallMs > 0 && manifest.EndedWallMs >= manifest.StartedWallMs)
		{
			manifest.ClaimedDurationMs = Math.Max(1L, manifest.EndedWallMs.Value - manifest.StartedWallMs);
		}
		ApplyCaptureFreshnessDiagnostics(manifest);
		string text5 = manifest.Displays.Select((SegmentDisplayManifest d) => d.CaptureSurfaceStaleReason).FirstOrDefault((string value2) => !string.IsNullOrWhiteSpace(value2));
		bool flag = !tuple.HasValue && SegmentFate.CandidateEligibility(session.SessionDir, manifest, diagnostic: false, fileReasonsPermanent: true).Kind == UploadEligibilityKind.Eligible;
		string text6 = null;
		string value;
		if (flag && text5 != null)
		{
			SegmentManifestStore.SaveCaptureFreshnessSuspect(session.SessionDir, session.SegmentIndex, manifest);
			text6 = "capture_freshness_stale:" + text5;
			value = "suspect reason=" + text5;
		}
		else if (flag)
		{
			SegmentManifestStore.SaveRecovered(session.SessionDir, session.SegmentIndex, manifest);
			value = "complete";
		}
		else
		{
			if (!ManifestHasCriticalFiles(session.SessionDir, manifest))
			{
				var (reason3, detail2) = tuple ?? (QuarantineReason.FileMissing, MissingCriticalFile(session.SessionDir, manifest) ?? "recovered segment manifest is incomplete");
				SegmentFate.Quarantine(session.SessionDir, session.SegmentIndex, manifest, reason3, FateOrigin.LaunchRecovery, detail2);
				return;
			}
			string text7;
			if (tuple.HasValue)
			{
				(QuarantineReason, string) valueOrDefault2 = tuple.GetValueOrDefault();
				text7 = valueOrDefault2.Item1.ToWire() + " " + valueOrDefault2.Item2;
			}
			else
			{
				text7 = "recovered segment timing evidence is incomplete";
			}
			text6 = text7;
			SegmentManifestStore.SaveSuspect(session.SessionDir, session.SegmentIndex, manifest, text6);
			value = "suspect reason=" + text6;
		}
		using (Telemetry.Scope(manifest.SessionId, manifest.SegmentIndex))
		{
			SegmentFate.EmitSealed(manifest, FateOrigin.LaunchRecovery);
			if (text6 != null)
			{
				Telemetry.Emit(Ev.SegmentSuspect(manifest.SessionId, manifest.SegmentIndex, text6));
			}
		}
		Log($"Interrupted session recovery disposition={value} session={fileName} seg={session.SegmentIndex}", "RecoverSegment", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Storage\\InterruptedSessionRecovery.cs", 432);
	}

	private static void ApplyCaptureFreshnessDiagnostics(SegmentManifest manifest)
	{
		foreach (SegmentDisplayManifest display in manifest.Displays)
		{
			if (!display.CaptureSurfaceStale && display.SourceFrameCount + display.CursorOnlyFrameCount + display.DuplicateFrameCount > 0)
			{
				string text = SegmentManifestStore.CaptureSurfaceStaleReason(display);
				if (text != null)
				{
					display.CaptureSurfaceStale = true;
					display.CaptureSurfaceStaleReason = text;
				}
			}
		}
	}

	internal static bool ManifestHasCriticalFiles(string sessionDir, SegmentManifest manifest)
	{
		if (SegmentFate.CandidateEligibility(sessionDir, manifest, diagnostic: true, fileReasonsPermanent: true).Kind == UploadEligibilityKind.Eligible)
		{
			return manifest.Displays.All((SegmentDisplayManifest display) => HasFinalizedVideoContainer(sessionDir, display.VideoFile));
		}
		return false;
	}

	internal static string? MissingCriticalFile(string sessionDir, SegmentManifest manifest)
	{
		UploadEligibility uploadEligibility = SegmentFate.CandidateEligibility(sessionDir, manifest, diagnostic: true, fileReasonsPermanent: true);
		if (uploadEligibility.Kind != UploadEligibilityKind.Eligible)
		{
			if (uploadEligibility.Reason != QuarantineReason.FileMissing)
			{
				return null;
			}
			return uploadEligibility.Detail;
		}
		return manifest.Displays.Select((SegmentDisplayManifest display) => display.VideoFile).FirstOrDefault((string video) => !HasFinalizedVideoContainer(sessionDir, video));
	}

	private static bool HasFinalizedVideoContainer(string sessionDir, string? fileName)
	{
		if (string.IsNullOrWhiteSpace(fileName))
		{
			return false;
		}
		try
		{
			return Mp4ContainerInspector.HasFinalizedContainer(Path.Combine(sessionDir, fileName));
		}
		catch
		{
			return false;
		}
	}

	private static bool CommittedNoFrame(string sessionDir, int segmentIndex, int displayIndex)
	{
		try
		{
			string text = Path.Combine(sessionDir, $"frame_timestamps_{displayIndex}_{segmentIndex}.jsonl");
			if (!File.Exists(text))
			{
				return false;
			}
			string path = Path.Combine(sessionDir, $"clip_{displayIndex}_{segmentIndex}.frames");
			if (File.Exists(path) && long.TryParse(File.ReadAllText(path).Trim(), out var result))
			{
				return result <= 0;
			}
			SegmentDisplayManifest segmentDisplayManifest = new SegmentDisplayManifest
			{
				DisplayIndex = displayIndex
			};
			SegmentSet.ApplySidecarStats(segmentDisplayManifest, text);
			return segmentDisplayManifest.EncodedFrameCount <= 0;
		}
		catch
		{
			return false;
		}
	}

	private static string VideoFileName(SegmentManifest manifest, int displayIndex, int segmentIndex)
	{
		string text = manifest.Displays.FirstOrDefault((SegmentDisplayManifest d) => d.DisplayIndex == displayIndex)?.VideoFile;
		if (text == null || text.Length <= 0)
		{
			return $"clip_{displayIndex}_{segmentIndex}.mp4";
		}
		return text;
	}

	private static List<string> LockedPayloadFiles(StorageManager.InterruptedSessionInfo session, SegmentManifest manifest, IEnumerable<int> displayIndexes)
	{
		List<string> list = new List<string>();
		foreach (int displayIndex in displayIndexes)
		{
			string[] array = new string[2]
			{
				VideoFileName(manifest, displayIndex, session.SegmentIndex),
				$"events_{displayIndex}_{session.SegmentIndex}.json"
			};
			foreach (string text in array)
			{
				if (IsHeldByAnotherProcess(Path.Combine(session.SessionDir, text)))
				{
					list.Add(text);
				}
			}
		}
		return list;
	}

	private static bool IsHeldByAnotherProcess(string path)
	{
		try
		{
			FileInfo fileInfo = new FileInfo(path);
			if (!fileInfo.Exists || fileInfo.Length == 0L)
			{
				return false;
			}
			using (File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
			{
				return false;
			}
		}
		catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
		{
			return true;
		}
		catch
		{
			return false;
		}
	}

	private static bool IsRecoverable(string videoPath)
	{
		try
		{
			FileInfo fileInfo = new FileInfo(videoPath);
			return fileInfo.Exists && fileInfo.Length > 0 && Mp4ContainerInspector.HasFinalizedContainer(videoPath);
		}
		catch
		{
			return false;
		}
	}

	internal static bool TryReadEventsTerminatorReason(string eventsPath, out string? reason)
	{
		reason = null;
		try
		{
			string text = File.ReadLines(eventsPath).Reverse().FirstOrDefault((string line) => !string.IsNullOrWhiteSpace(line));
			if (text == null)
			{
				return false;
			}
			using JsonDocument jsonDocument = JsonDocument.Parse(text);
			if (!jsonDocument.RootElement.TryGetProperty("type", out var value) || value.ValueKind != JsonValueKind.String || !string.Equals(value.GetString(), "segment_end", StringComparison.Ordinal))
			{
				return false;
			}
			if (jsonDocument.RootElement.TryGetProperty("reason", out var value2) && value2.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value2.GetString()))
			{
				reason = value2.GetString();
			}
			return true;
		}
		catch
		{
			return false;
		}
	}

	private static void AppendSegmentEnd(string eventsPath, int segmentIndex, int displayIndex, long timestampMs)
	{
		string text = JsonSerializer.Serialize(new
		{
			type = "segment_end",
			timestamp = timestampMs,
			reason = "capture_failure",
			source = "interrupted_session_recovery",
			error = new
			{
				domain = "KuroWindows.Storage.InterruptedSessionRecovery",
				code = 0,
				name = "InterruptedSessionRecovered",
				description = "Segment was finalized on next launch from last committed local timing evidence."
			},
			segmentIndex = segmentIndex,
			displayIndex = displayIndex
		});
		bool flag = false;
		try
		{
			using FileStream fileStream = new FileStream(eventsPath, FileMode.Open, FileAccess.Read, FileShare.Read);
			if (fileStream.Length > 0)
			{
				fileStream.Seek(-1L, SeekOrigin.End);
				flag = fileStream.ReadByte() != 10;
			}
		}
		catch
		{
		}
		File.AppendAllText(eventsPath, flag ? (Environment.NewLine + text + Environment.NewLine) : (text + Environment.NewLine));
	}

	private static bool CanResolveRecoveredSegmentEndTimestamp(string sessionDir, int segmentIndex, int displayIndex, string eventsPath, out long timestampMs)
	{
		double? num = TryReadLastJsonNumber(Path.Combine(sessionDir, $"frame_timestamps_{displayIndex}_{segmentIndex}.jsonl"), "wall_ms");
		if (num.HasValue)
		{
			timestampMs = (long)num.Value;
			return true;
		}
		double? num2 = TryReadLastJsonNumber(eventsPath, "timestamp");
		if (num2.HasValue)
		{
			timestampMs = (long)num2.Value;
			return true;
		}
		timestampMs = 0L;
		return false;
	}

	private static double? TryReadLastJsonNumber(string path, string propertyName)
	{
		if (!File.Exists(path))
		{
			return null;
		}
		try
		{
			foreach (string item in File.ReadLines(path).Reverse())
			{
				if (string.IsNullOrWhiteSpace(item))
				{
					continue;
				}
				try
				{
					using JsonDocument jsonDocument = JsonDocument.Parse(item);
					if (jsonDocument.RootElement.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var value2))
					{
						return value2;
					}
				}
				catch
				{
				}
			}
		}
		catch
		{
		}
		return null;
	}

	private static long? TryGetRecoveredEventDurationMs(string sessionDir, SegmentManifest manifest)
	{
		long? num = (from display in manifest.Displays
			select TryReadEventSpanMs(Path.Combine(sessionDir, display.EventsFile)) into v
			where v.HasValue && v.GetValueOrDefault() > 0
			select v).DefaultIfEmpty().Max();
		if (!num.HasValue || num.GetValueOrDefault() <= 0)
		{
			return null;
		}
		return num.Value;
	}

	private static long? TryReadEventSpanMs(string path)
	{
		long? num = SegmentSet.ReadEventSpanMs(path);
		if (!num.HasValue)
		{
			return null;
		}
		return Math.Max(1L, num.Value);
	}

	private static void UpdateDisplayManifest(SegmentManifest manifest, int segmentIndex, int displayIndex, string videoPath, string eventsPath)
	{
		SegmentDisplayManifest segmentDisplayManifest = manifest.Displays.FirstOrDefault((SegmentDisplayManifest d) => d.DisplayIndex == displayIndex);
		if (segmentDisplayManifest == null)
		{
			segmentDisplayManifest = new SegmentDisplayManifest
			{
				DisplayIndex = displayIndex
			};
			manifest.Displays.Add(segmentDisplayManifest);
		}
		if (segmentDisplayManifest.Backend == CaptureBackendKind.Auto)
		{
			segmentDisplayManifest.Backend = CaptureBackendKind.DesktopDuplication;
		}
		segmentDisplayManifest.VideoFile = Path.GetFileName(videoPath);
		segmentDisplayManifest.EventsFile = Path.GetFileName(eventsPath);
		string text = $"frame_timestamps_{displayIndex}_{segmentIndex}.jsonl";
		string text2 = Path.Combine(Path.GetDirectoryName(videoPath), text);
		if (File.Exists(text2))
		{
			segmentDisplayManifest.FrameSidecarFile = text;
			ApplySidecarStats(segmentDisplayManifest, text2);
		}
	}

	private static void ApplySidecarStats(SegmentDisplayManifest display, string sidecarPath)
	{
		SegmentSet.ApplySidecarStats(display, sidecarPath);
	}

	private static long? GetEncodedDurationMs(SegmentDisplayManifest display)
	{
		return SegmentSet.GetEncodedDurationMs(display);
	}

	private static void Log(string message, [CallerMemberName] string memberName = "", [CallerFilePath] string filePath = "", [CallerLineNumber] int lineNumber = 0)
	{
		KuroLog.Recording(message, memberName, filePath, lineNumber);
	}
}
