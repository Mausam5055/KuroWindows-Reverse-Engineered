using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClientTelemetry;
using KuroWindows.Upload;

namespace KuroWindows.RecorderCore.Segments;

internal static class SegmentFate
{
	internal static readonly TimeSpan QuarantinedRetention = TimeSpan.FromDays(14.0);

	internal static readonly TimeSpan FileCheckGrace = TimeSpan.FromMinutes(15.0);

	private static readonly Dictionary<string, QuarantineReason> ReasonsByWire = Enum.GetValues<QuarantineReason>().ToDictionary((QuarantineReason reason) => reason.ToWire(), StringComparer.Ordinal);

	public static SegmentManifest? Quarantine(string sessionDir, int segmentIndex, SegmentManifest? manifest, QuarantineReason reason, FateOrigin origin, string? detail = null, long? attempts = null)
	{
		try
		{
			if (manifest == null)
			{
				manifest = SegmentManifestStore.TryLoad(SegmentManifestStore.ManifestPath(sessionDir, segmentIndex)) ?? new SegmentManifest
				{
					StartedWallMs = NewestArtifactWallMs(sessionDir, segmentIndex)
				};
			}
			if (string.IsNullOrWhiteSpace(manifest.SessionId))
			{
				manifest.SessionId = Path.GetFileName(sessionDir);
			}
			manifest.SegmentIndex = segmentIndex;
			manifest.State = SegmentManifestState.Quarantined;
			manifest.QuarantineReason = reason.ToWire();
			if (!new SegmentManifestStore(sessionDir, segmentIndex).TrySave(manifest, out Exception error))
			{
				KuroLog.LocalOnly("upload", "segment manifest save failed file=" + SegmentManifestStore.ManifestFileName(segmentIndex) + " error=" + KuroLog.ExceptionSummary(error), "Quarantine", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\RecorderCore\\Segments\\SegmentFate.cs", 90);
				return null;
			}
			using (Telemetry.Scope(manifest.SessionId, segmentIndex))
			{
				Telemetry.Emit(Ev.SegmentQuarantined(manifest.SessionId, segmentIndex, reason.ToWire(), origin, detail, attempts));
			}
			KuroLog.Upload($"segment quarantined session={manifest.SessionId} seg={segmentIndex} reason={reason.ToWire()} origin={origin.ToWire()} detail=\"{Quoted(detail)}\"", "Quarantine", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\RecorderCore\\Segments\\SegmentFate.cs", 100);
			return manifest;
		}
		catch (Exception ex)
		{
			KuroLog.LocalOnly("upload", $"segment quarantine failed session={Path.GetFileName(sessionDir)} seg={segmentIndex} reason={reason.ToWire()} origin={origin.ToWire()} error={KuroLog.ExceptionSummary(ex)}", "Quarantine", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\RecorderCore\\Segments\\SegmentFate.cs", 107);
			return null;
		}
	}

	public static (List<(string Name, string Error)> Failures, long Bytes) DeleteFiles(string sessionDir, int segmentIndex, SegmentManifest? manifest = null, bool keepManifestOnFailure = true)
	{
		List<(string, string)> list = new List<(string, string)>();
		long bytes = 0L;
		List<FileSystemInfo> list2 = new List<FileSystemInfo>();
		List<FileSystemInfo> list3 = new List<FileSystemInfo>();
		List<FileSystemInfo> list4 = new List<FileSystemInfo>(1);
		HashSet<string> hashSet = null;
		if (manifest != null)
		{
			hashSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			foreach (SegmentDisplayManifest display in manifest.Displays)
			{
				AddName(hashSet, display.VideoFile);
				AddName(hashSet, display.EventsFile);
				AddName(hashSet, display.FrameSidecarFile);
			}
			AddName(hashSet, manifest.AudioFile);
		}
		try
		{
			FileSystemInfo[] array = new DirectoryInfo(sessionDir).EnumerateFileSystemInfos().ToArray();
			foreach (FileSystemInfo fileSystemInfo in array)
			{
				bool flag = SegmentManifestStore.TryParseSegmentIndex(fileSystemInfo.Name, out var segmentIndex2, out var kind) && segmentIndex2 == segmentIndex;
				if (flag || (hashSet != null && hashSet.Contains(fileSystemInfo.Name)))
				{
					if (flag && kind != SegmentArtifactKind.Other)
					{
						(kind switch
						{
							SegmentArtifactKind.UploadState => list2, 
							SegmentArtifactKind.ManifestTemp => list3, 
							_ => list4, 
						}).Add(fileSystemInfo);
					}
					else
					{
						Remove(fileSystemInfo, list, ref bytes);
					}
				}
			}
		}
		catch (DirectoryNotFoundException)
		{
			return (Failures: list, Bytes: bytes);
		}
		catch (Exception ex2)
		{
			list.Add((Path.GetFileName(sessionDir), ex2.Message));
		}
		List<FileSystemInfo>[] array2 = new List<FileSystemInfo>[3] { list2, list3, list4 };
		foreach (List<FileSystemInfo> list5 in array2)
		{
			if (keepManifestOnFailure && list.Count > 0)
			{
				return (Failures: list, Bytes: bytes);
			}
			foreach (FileSystemInfo item in list5)
			{
				Remove(item, list, ref bytes);
			}
		}
		if (!keepManifestOnFailure || list.Count == 0)
		{
			UploadMarkerFiles.ClearSegmentMarkers(sessionDir, segmentIndex);
		}
		return (Failures: list, Bytes: bytes);
	}

	public static long? Delete(string sessionDir, int segmentIndex, DeleteCause cause, SegmentManifest? manifest = null, UploadManifest? uploadState = null, double? waitedHours = null, string? detail = null)
	{
		string text = SessionIdOf(sessionDir, manifest);
		try
		{
			var (list, value) = DeleteFiles(sessionDir, segmentIndex, manifest);
			if (list.Count > 0)
			{
				KuroLog.LocalOnly("upload", $"segment delete incomplete session={text} seg={segmentIndex} cause={cause.ToWire()} failed={list.Count} first={list[0].Item1}: {list[0].Item2}", "Delete", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\RecorderCore\\Segments\\SegmentFate.cs", 244);
				return null;
			}
			QuarantineReason? quarantineReason = null;
			if (cause == DeleteCause.QuarantineExpired)
			{
				string text2 = manifest?.QuarantineReason;
				if (text2 != null && ReasonsByWire.TryGetValue(text2, out var value2))
				{
					quarantineReason = value2;
				}
				else
				{
					quarantineReason = QuarantineReason.Legacy;
					if (detail == null)
					{
						detail = manifest?.QuarantineReason;
					}
				}
			}
			SegmentDisposition? disposition = ((cause == DeleteCause.BacklogPurged && manifest != null) ? new SegmentDisposition?((manifest.State == SegmentManifestState.Suspect) ? SegmentDisposition.Suspect : SegmentDisposition.Complete) : ((SegmentDisposition?)null));
			using (Telemetry.Scope(text, segmentIndex))
			{
				Telemetry.Emit(Ev.SegmentDeleted(text, cause, segmentIndex, quarantineReason, disposition, uploadState?.Attempts, uploadState?.LastErrorCode, waitedHours, value, detail));
			}
			Count.SegmentDeleted(cause, 1L);
			string[] array = new string[6]
			{
				$"segment deleted session={text} seg={segmentIndex} cause={cause.ToWire()} bytes={value}",
				null,
				null,
				null,
				null,
				null
			};
			string text3;
			if (quarantineReason.HasValue)
			{
				QuarantineReason valueOrDefault = quarantineReason.GetValueOrDefault();
				text3 = " quarantine_reason=" + valueOrDefault.ToWire();
			}
			else
			{
				text3 = "";
			}
			array[1] = text3;
			string text4;
			if (disposition.HasValue)
			{
				SegmentDisposition valueOrDefault2 = disposition.GetValueOrDefault();
				text4 = " disposition=" + valueOrDefault2.ToWire();
			}
			else
			{
				text4 = "";
			}
			array[2] = text4;
			string text5;
			if (uploadState != null)
			{
				text5 = $" attempts={uploadState.Attempts} last_error_code={uploadState.LastErrorCode ?? "-"}";
			}
			else
			{
				text5 = "";
			}
			array[3] = text5;
			string text6;
			if (waitedHours.HasValue)
			{
				double valueOrDefault3 = waitedHours.GetValueOrDefault();
				text6 = $" waited_hours={valueOrDefault3:F1}";
			}
			else
			{
				text6 = "";
			}
			array[4] = text6;
			array[5] = ((detail == null) ? "" : (" detail=\"" + Quoted(detail) + "\""));
			KuroLog.LocalOnly("upload", string.Concat(array), "Delete", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\RecorderCore\\Segments\\SegmentFate.cs", 284);
			return value;
		}
		catch (Exception ex)
		{
			KuroLog.LocalOnly("upload", $"segment delete failed session={text} seg={segmentIndex} cause={cause.ToWire()} error={KuroLog.ExceptionSummary(ex)}", "Delete", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\RecorderCore\\Segments\\SegmentFate.cs", 296);
			return null;
		}
	}

	public static bool DeleteDirectory(string sessionDir, long bytes)
	{
		string fileName = Path.GetFileName(sessionDir);
		try
		{
			Directory.Delete(sessionDir, recursive: true);
		}
		catch (Exception ex)
		{
			KuroLog.LocalOnly("upload", "session debris delete failed session=" + fileName + " error=" + KuroLog.ExceptionSummary(ex), "DeleteDirectory", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\RecorderCore\\Segments\\SegmentFate.cs", 316);
			return false;
		}
		using (Telemetry.Scope(fileName, null))
		{
			long? bytes2 = bytes;
			Telemetry.Emit(Ev.SegmentDeleted(fileName, DeleteCause.DebrisExpired, null, null, null, null, null, null, bytes2));
		}
		Count.SegmentDeleted(DeleteCause.DebrisExpired, 1L);
		KuroLog.LocalOnly("upload", $"segment deleted session={fileName} cause=debris_expired bytes={bytes}", "DeleteDirectory", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\RecorderCore\\Segments\\SegmentFate.cs", 325);
		return true;
	}

	public static bool RemoveSessionDirIfEmpty(string sessionDir)
	{
		try
		{
			List<string> list = null;
			foreach (string item in Directory.EnumerateFileSystemEntries(sessionDir))
			{
				string fileName = Path.GetFileName(item);
				if (!UploadMarkerFiles.IsUploadMarkerFile(fileName) && !fileName.StartsWith("metadata_", StringComparison.OrdinalIgnoreCase))
				{
					return false;
				}
				(list ?? (list = new List<string>())).Add(item);
			}
			if (list != null)
			{
				foreach (string item2 in list)
				{
					File.Delete(item2);
				}
			}
			Directory.Delete(sessionDir);
			return true;
		}
		catch (DirectoryNotFoundException)
		{
			return true;
		}
		catch (Exception ex2)
		{
			KuroLog.LocalOnly("upload", "session dir not removed session=" + Path.GetFileName(sessionDir) + " error=" + ex2.Message, "RemoveSessionDirIfEmpty", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\RecorderCore\\Segments\\SegmentFate.cs", 368);
			return false;
		}
	}

	internal static UploadEligibility UploadEligibilityFor(string sessionDir, SegmentManifestLoadResult load, DateTime nowUtc)
	{
		SegmentManifest manifest = load.Manifest;
		if (manifest == null)
		{
			return UploadEligibility.Withheld;
		}
		if (SegmentManifestStore.IsInterruptedState(manifest.State))
		{
			return UploadEligibility.Wait;
		}
		if (!SegmentManifestStore.IsUploadCandidateState(manifest.State))
		{
			return UploadEligibility.Withheld;
		}
		return CandidateEligibility(sessionDir, manifest, SegmentManifestStore.IsDiagnosticUploadState(manifest.State), nowUtc - load.LastWriteUtc >= FileCheckGrace);
	}

	internal static UploadEligibility CandidateEligibility(string sessionDir, SegmentManifest manifest, bool diagnostic, bool fileReasonsPermanent)
	{
		if (manifest.Displays.Count == 0)
		{
			return UploadEligibility.Quarantine(QuarantineReason.NoDisplays);
		}
		long? claimedDurationMs = manifest.ClaimedDurationMs;
		if (!claimedDurationMs.HasValue || claimedDurationMs.GetValueOrDefault() <= 0)
		{
			return UploadEligibility.Quarantine(QuarantineReason.DurationZero);
		}
		foreach (SegmentDisplayManifest display in manifest.Displays)
		{
			if (!diagnostic)
			{
				if (display.EncodedFrameCount <= 0)
				{
					return UploadEligibility.Quarantine(QuarantineReason.ZeroFrames, $"display {display.DisplayIndex}");
				}
				if (!string.IsNullOrWhiteSpace(display.SidecarIntegrityError))
				{
					return UploadEligibility.Quarantine(QuarantineReason.SidecarInvalid, display.FrameSidecarFile + ": " + display.SidecarIntegrityError);
				}
			}
			string text;
			if (SegmentManifestStore.HasNonEmptyFile(sessionDir, display.VideoFile))
			{
				if (SegmentManifestStore.HasNonEmptyFile(sessionDir, display.EventsFile))
				{
					text = ((!diagnostic && !SegmentManifestStore.HasNonEmptyFile(sessionDir, display.FrameSidecarFile)) ? NameOrProse(display.FrameSidecarFile, "frame sidecar", display.DisplayIndex) : null);
				}
				else
				{
					text = NameOrProse(display.EventsFile, "events", display.DisplayIndex);
				}
			}
			else
			{
				text = NameOrProse(display.VideoFile, "video", display.DisplayIndex);
			}
			if (text != null)
			{
				return fileReasonsPermanent ? UploadEligibility.Quarantine(QuarantineReason.FileMissing, text) : UploadEligibility.Wait;
			}
		}
		return UploadEligibility.Eligible;
	}

	internal static void EmitSealed(SegmentManifest manifest, FateOrigin origin)
	{
		long valueOrDefault = manifest.ClaimedDurationMs.GetValueOrDefault();
		SegmentDisplayManifest segmentDisplayManifest = ((manifest.Displays.Count > 0) ? manifest.Displays[0] : null);
		Count.SegmentSealed(1L);
		Count.RecordingMinutes((valueOrDefault + 30000) / 60000);
		string sessionId = manifest.SessionId;
		long segmentIndex = manifest.SegmentIndex;
		bool disposition = manifest.State == SegmentManifestState.Suspect;
		long? num = manifest.Displays.Count;
		long? maskTriggers = ((manifest.MaskTrigger != null) ? 1 : 0);
		long? displayCount = num;
		RecordingCore? recorderCore = TelemetryMap.RecordingCoreFor(manifest.Displays);
		CaptureSource? captureSource = TelemetryMap.CaptureSourceFor(manifest.Displays);
		EncoderKind? encoder = TelemetryMap.EncoderKindFor(manifest.Displays);
		long? frames = ((segmentDisplayManifest != null && segmentDisplayManifest.EncodedFrameCount > 0) ? new long?(segmentDisplayManifest.EncodedFrameCount) : ((long?)null));
		long? freshFrames = ((segmentDisplayManifest != null && segmentDisplayManifest.SourceFrameCount >= 0) ? new long?(segmentDisplayManifest.SourceFrameCount) : ((long?)null));
		AudioState? audioState = TelemetryMap.AudioStateFor(manifest.Audio?.State);
		Telemetry.Emit(Ev.SegmentSealed(audioReason: manifest.Audio?.Reason, recordingId: sessionId, segmentIndex: segmentIndex, disposition: disposition ? SegmentDisposition.Suspect : SegmentDisposition.Complete, durationMs: valueOrDefault, maskTriggers: maskTriggers, displayCount: displayCount, origin: origin, deviceModel: null, thermalMax: null, thermalThrottledMs: null, thermalHeadroomMax: null, appCpuShare: null, helperCpuShare: null, appMemoryMb: null, helperMemoryMb: null, lowMemoryEvents: null, frames: frames, framesDropped: null, maxGapMs: null, encoderResets: null, uiResponseP95Ms: null, batteryStart: null, batteryEnd: null, chargingMs: null, audioState: audioState, recorderCore: recorderCore, captureSource: captureSource, encoder: encoder, freshFrames: freshFrames));
	}

	private static void Remove(FileSystemInfo entry, List<(string Name, string Error)> failures, ref long bytes)
	{
		try
		{
			long num = ((entry is FileInfo fileInfo) ? fileInfo.Length : 0);
			File.Delete(entry.FullName);
			bytes += num;
		}
		catch (Exception ex)
		{
			failures.Add((entry.Name, ex.Message));
		}
	}

	private static string NameOrProse(string? fileName, string kind, int displayIndex)
	{
		if (!string.IsNullOrWhiteSpace(fileName))
		{
			return fileName;
		}
		return $"{kind} for display {displayIndex}";
	}

	private static void AddName(HashSet<string> names, string? fileName)
	{
		if (!string.IsNullOrWhiteSpace(fileName))
		{
			names.Add(fileName);
		}
	}

	private static long NewestArtifactWallMs(string sessionDir, int segmentIndex)
	{
		DateTime dateTime = DateTime.MinValue;
		try
		{
			foreach (FileInfo item in new DirectoryInfo(sessionDir).EnumerateFiles())
			{
				if (SegmentManifestStore.TryParseSegmentIndex(item.Name, out var segmentIndex2, out var _) && segmentIndex2 == segmentIndex && item.LastWriteTimeUtc > dateTime)
				{
					dateTime = item.LastWriteTimeUtc;
				}
			}
		}
		catch
		{
		}
		return new DateTimeOffset((dateTime == DateTime.MinValue) ? DateTime.UtcNow : dateTime).ToUnixTimeMilliseconds();
	}

	private static string SessionIdOf(string sessionDir, SegmentManifest? manifest)
	{
		string text = manifest?.SessionId;
		if (text == null || text.Length <= 0)
		{
			return Path.GetFileName(sessionDir);
		}
		return text;
	}

	private static string Quoted(string? detail)
	{
		if (detail != null)
		{
			return detail.Replace('"', '\'');
		}
		return "";
	}
}
