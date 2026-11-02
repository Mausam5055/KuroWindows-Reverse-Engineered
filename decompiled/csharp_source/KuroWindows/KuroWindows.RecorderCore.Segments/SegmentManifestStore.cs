using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KuroWindows.RecorderCore.Segments;

public sealed class SegmentManifestStore
{
	internal const long CaptureStaleHighConfidenceActiveInputThreshold = 500L;

	internal const long CaptureStaleMinimumEvidenceDurationMs = 120000L;

	internal const long CaptureStaleFillerFramePercent = 98L;

	private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
	{
		WriteIndented = true,
		PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
		Converters = { (JsonConverter)new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
	};

	private static readonly ConcurrentDictionary<string, byte> LoggedParseFailures = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);

	internal const string InterruptedRecoveryEndSource = "interrupted_session_recovery";

	private readonly string _path;

	private readonly object _sync = new object();

	private static readonly (string Prefix, string[] Extensions)[] ArtifactNames = new (string, string[])[4]
	{
		("clip_", new string[2] { "mp4", "frames" }),
		("events_", new string[1] { "json" }),
		("frame_timestamps_", new string[1] { "jsonl" }),
		("metadata_", new string[1] { "json" })
	};

	public string Path => _path;

	public SegmentManifestStore(string sessionDir, int segmentIndex)
	{
		_path = ManifestPath(sessionDir, segmentIndex);
	}

	public static string ManifestFileName(int segmentIndex)
	{
		return $"segment_{segmentIndex}.manifest.json";
	}

	public static string ManifestPath(string sessionDir, int segmentIndex)
	{
		return System.IO.Path.Combine(sessionDir, ManifestFileName(segmentIndex));
	}

	internal static bool TryParseSegmentIndex(string fileName, out int segmentIndex, out SegmentArtifactKind kind)
	{
		segmentIndex = 0;
		kind = SegmentArtifactKind.Other;
		if (fileName.StartsWith("audio_", StringComparison.OrdinalIgnoreCase))
		{
			ReadOnlySpan<char> span = fileName.AsSpan("audio_".Length);
			int num = span.IndexOf('.');
			if (num <= 0 || !TryParseIndex(span.Slice(0, num), out segmentIndex) || !MemoryExtensions.Equals(span.Slice(num + 1), "mp4", StringComparison.OrdinalIgnoreCase))
			{
				segmentIndex = 0;
				return false;
			}
			return true;
		}
		if (fileName.StartsWith("segment_", StringComparison.OrdinalIgnoreCase))
		{
			ReadOnlySpan<char> span2 = fileName.AsSpan("segment_".Length);
			int num2 = span2.IndexOf('.');
			if (num2 <= 0 || !TryParseIndex(span2.Slice(0, num2), out segmentIndex))
			{
				return false;
			}
			ReadOnlySpan<char> readOnlySpan = span2.Slice(num2);
			if (MemoryExtensions.Equals(readOnlySpan, ".manifest.json", StringComparison.OrdinalIgnoreCase))
			{
				kind = SegmentArtifactKind.Manifest;
				return true;
			}
			if (IsTemp(readOnlySpan, ".manifest.json."))
			{
				kind = SegmentArtifactKind.ManifestTemp;
				return true;
			}
			if (MemoryExtensions.Equals(readOnlySpan, ".upload.json", StringComparison.OrdinalIgnoreCase) || IsTemp(readOnlySpan, ".upload.json."))
			{
				kind = SegmentArtifactKind.UploadState;
				return true;
			}
			return false;
		}
		(string, string[])[] artifactNames = ArtifactNames;
		for (int i = 0; i < artifactNames.Length; i++)
		{
			var (text, array) = artifactNames[i];
			if (!fileName.StartsWith(text, StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}
			ReadOnlySpan<char> span3 = fileName.AsSpan(text.Length);
			int num3 = span3.IndexOf('.');
			if (num3 <= 0)
			{
				return false;
			}
			ReadOnlySpan<char> span4 = span3.Slice(0, num3);
			int num4 = span4.IndexOf('_');
			if (num4 <= 0 || !IsDigits(span4.Slice(0, num4)) || !TryParseIndex(span4.Slice(num4 + 1), out segmentIndex))
			{
				return false;
			}
			ReadOnlySpan<char> span5 = span3.Slice(num3 + 1);
			string[] array2 = array;
			foreach (string text2 in array2)
			{
				if (MemoryExtensions.Equals(span5, text2, StringComparison.OrdinalIgnoreCase))
				{
					return true;
				}
			}
			return false;
		}
		return false;
	}

	internal static bool IsAudioFile(string fileName, out int segmentIndex)
	{
		if (TryParseSegmentIndex(fileName, out segmentIndex, out var kind) && kind == SegmentArtifactKind.Other && fileName.StartsWith("audio_", StringComparison.OrdinalIgnoreCase))
		{
			return true;
		}
		segmentIndex = 0;
		return false;
	}

	internal static bool TryParseManifestSegmentIndex(string fileName, out int segmentIndex)
	{
		if (TryParseSegmentIndex(fileName, out segmentIndex, out var kind) && kind == SegmentArtifactKind.Manifest)
		{
			return true;
		}
		segmentIndex = 0;
		return false;
	}

	internal static bool TryParseClipDisplayIndex(string fileName, out int displayIndex, out int segmentIndex)
	{
		displayIndex = 0;
		if (!TryParseSegmentIndex(fileName, out segmentIndex, out var kind) || kind != SegmentArtifactKind.Other || !fileName.StartsWith("clip_", StringComparison.OrdinalIgnoreCase) || !fileName.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
		{
			segmentIndex = 0;
			return false;
		}
		ReadOnlySpan<char> span = fileName.AsSpan("clip_".Length);
		if (int.TryParse(span.Slice(0, span.IndexOf('_')), out displayIndex))
		{
			return true;
		}
		segmentIndex = 0;
		return false;
	}

	private static bool IsTemp(ReadOnlySpan<char> suffix, string head)
	{
		if (suffix.Length > head.Length + 4 && suffix.StartsWith(head, StringComparison.OrdinalIgnoreCase))
		{
			return suffix.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase);
		}
		return false;
	}

	private static bool TryParseIndex(ReadOnlySpan<char> digits, out int index)
	{
		index = 0;
		if (IsDigits(digits) && (digits.Length == 1 || digits[0] != '0'))
		{
			return int.TryParse(digits, out index);
		}
		return false;
	}

	private static bool IsDigits(ReadOnlySpan<char> text)
	{
		if (text.IsEmpty)
		{
			return false;
		}
		ReadOnlySpan<char> readOnlySpan = text;
		for (int i = 0; i < readOnlySpan.Length; i++)
		{
			if (!char.IsAsciiDigit(readOnlySpan[i]))
			{
				return false;
			}
		}
		return true;
	}

	internal static bool IsInterruptedState(SegmentManifestState state)
	{
		if ((uint)state <= 1u)
		{
			return true;
		}
		return false;
	}

	internal static bool IsUploadCandidateState(SegmentManifestState state)
	{
		if ((uint)(state - 2) <= 2u)
		{
			return true;
		}
		return false;
	}

	internal static bool IsDiagnosticUploadState(SegmentManifestState state)
	{
		return state == SegmentManifestState.Suspect;
	}

	internal static bool IsRecovered(SegmentManifest? manifest)
	{
		if (manifest == null || manifest.State != SegmentManifestState.Recovered)
		{
			return string.Equals(manifest?.EndSource, "interrupted_session_recovery", StringComparison.OrdinalIgnoreCase);
		}
		return true;
	}

	public void Save(SegmentManifest manifest)
	{
		lock (_sync)
		{
			Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path));
			string text = $"{_path}.{Environment.ProcessId}.tmp";
			File.WriteAllText(text, JsonSerializer.Serialize(manifest, JsonOptions));
			File.Move(text, _path, overwrite: true);
		}
	}

	public bool TrySave(SegmentManifest manifest, out Exception? error)
	{
		try
		{
			Save(manifest);
			error = null;
			return true;
		}
		catch (Exception ex)
		{
			error = ex;
			return false;
		}
	}

	public static SegmentManifest? TryLoad(string path)
	{
		return TryLoadDetailed(path).Manifest;
	}

	internal static SegmentManifestLoadResult TryLoadDetailed(string path)
	{
		FileInfo fileInfo = null;
		try
		{
			fileInfo = new FileInfo(path);
			if (!fileInfo.Exists)
			{
				return new SegmentManifestLoadResult(null, Exists: false, Corrupt: false, default);
			}
			SegmentManifest segmentManifest = JsonSerializer.Deserialize<SegmentManifest>(File.ReadAllText(path), JsonOptions);
			if (segmentManifest == null)
			{
				throw new JsonException("manifest JSON was null");
			}
			List<SegmentDisplayManifest> displays = segmentManifest.Displays;
			if (displays != null)
			{
				displays.RemoveAll((SegmentDisplayManifest display) => display == null);
			}
			else
			{
				segmentManifest.Displays = new List<SegmentDisplayManifest>();
			}
			LoggedParseFailures.TryRemove(path, out var _);
			return new SegmentManifestLoadResult(segmentManifest, Exists: true, Corrupt: false, fileInfo.LastWriteTimeUtc);
		}
		catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
		{
			if (fileInfo == null)
			{
				return new SegmentManifestLoadResult(null, Exists: false, Corrupt: false, default);
			}
			return new SegmentManifestLoadResult(null, Exists: true, Corrupt: false, fileInfo.LastWriteTimeUtc, ex);
		}
		catch (Exception ex2)
		{
			if (fileInfo == null)
			{
				return new SegmentManifestLoadResult(null, Exists: false, Corrupt: false, default);
			}
			if (LoggedParseFailures.TryAdd(path, 0))
			{
				KuroLog.Upload("segment manifest parse failed file=" + System.IO.Path.GetFileName(path) + ": " + ex2.GetType().Name, "TryLoadDetailed", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\RecorderCore\\Segments\\SegmentManifest.cs", 647);
			}
			return new SegmentManifestLoadResult(null, Exists: true, Corrupt: true, fileInfo.LastWriteTimeUtc);
		}
	}

	public static string? CaptureSurfaceStaleReason(SegmentDisplayManifest display)
	{
		if (display.EncodedFrameCount <= 0 || display.ActiveInputEventCount < 500 || EncodedDurationMs(display) < 120000)
		{
			return null;
		}
		long? nonFabricatedFrameCount = display.NonFabricatedFrameCount;
		if (nonFabricatedFrameCount.HasValue)
		{
			long valueOrDefault = nonFabricatedFrameCount.GetValueOrDefault();
			nonFabricatedFrameCount = display.FabricatedFrameCount;
			if (nonFabricatedFrameCount.HasValue)
			{
				long valueOrDefault2 = nonFabricatedFrameCount.GetValueOrDefault();
				if (valueOrDefault <= 0)
				{
					return $"capture surface stale for display {display.DisplayIndex}: {display.ActiveInputEventCount} active input events but no source desktop frames";
				}
				long num = EncodedDurationMs(display) * 1000000;
				long valueOrDefault3 = display.FabricatedFrameDurationNs.GetValueOrDefault();
				bool flag = num > 0 && (display.NonFabricatedFrameDurationNs.GetValueOrDefault() > 0 || valueOrDefault3 > 0);
				if (flag && valueOrDefault3 * 100 >= num * 98)
				{
					return $"capture surface stale for display {display.DisplayIndex}: {display.ActiveInputEventCount} active input events while {valueOrDefault3 / 1000000}/{num / 1000000}ms reused the last source frame";
				}
				if (!flag && valueOrDefault2 * 100 >= display.EncodedFrameCount * 98)
				{
					return $"capture surface stale for display {display.DisplayIndex}: {display.ActiveInputEventCount} active input events while {valueOrDefault2}/{display.EncodedFrameCount} frames reused the last source frame";
				}
				return null;
			}
		}
		return null;
	}

	private static long EncodedDurationMs(SegmentDisplayManifest display)
	{
		if (display.FirstPtsNs.HasValue && display.LastPtsNs.HasValue && display.LastPtsNs.Value >= display.FirstPtsNs.Value)
		{
			int num = ((display.CaptureFps > 0) ? display.CaptureFps : 30);
			long? lastFrameDurationNs = display.LastFrameDurationNs;
			long num2 = ((lastFrameDurationNs.HasValue && lastFrameDurationNs.GetValueOrDefault() > 0) ? display.LastFrameDurationNs.Value : (1000000000L / (long)num));
			return (display.LastPtsNs.Value - display.FirstPtsNs.Value + num2 + 999999) / 1000000;
		}
		if (display.CaptureFps <= 0)
		{
			return 0L;
		}
		return display.EncodedFrameCount * 1000 / display.CaptureFps;
	}

	public static void SaveRecovered(string sessionDir, int segmentIndex, SegmentManifest manifest)
	{
		manifest.State = SegmentManifestState.Complete;
		manifest.EndSource = "interrupted_session_recovery";
		manifest.SessionId = (string.IsNullOrWhiteSpace(manifest.SessionId) ? System.IO.Path.GetFileName(sessionDir) : manifest.SessionId);
		manifest.SegmentIndex = segmentIndex;
		new SegmentManifestStore(sessionDir, segmentIndex).Save(manifest);
	}

	public static void SaveSuspect(string sessionDir, int segmentIndex, SegmentManifest manifest, string reason)
	{
		manifest.State = SegmentManifestState.Suspect;
		manifest.SessionId = (string.IsNullOrWhiteSpace(manifest.SessionId) ? System.IO.Path.GetFileName(sessionDir) : manifest.SessionId);
		manifest.SegmentIndex = segmentIndex;
		manifest.QuarantineReason = null;
		foreach (SegmentDisplayManifest display in manifest.Displays)
		{
			display.TimingSuspect = true;
			SegmentDisplayManifest segmentDisplayManifest = display;
			if (segmentDisplayManifest.TimingSuspectReason == null)
			{
				string text = (segmentDisplayManifest.TimingSuspectReason = reason);
			}
		}
		new SegmentManifestStore(sessionDir, segmentIndex).Save(manifest);
	}

	public static void SaveCaptureFreshnessSuspect(string sessionDir, int segmentIndex, SegmentManifest manifest)
	{
		manifest.State = SegmentManifestState.Suspect;
		manifest.SessionId = (string.IsNullOrWhiteSpace(manifest.SessionId) ? System.IO.Path.GetFileName(sessionDir) : manifest.SessionId);
		manifest.SegmentIndex = segmentIndex;
		manifest.QuarantineReason = null;
		new SegmentManifestStore(sessionDir, segmentIndex).Save(manifest);
	}

	internal static bool HasNonEmptyFile(string sessionDir, string? fileName)
	{
		if (string.IsNullOrWhiteSpace(fileName))
		{
			return false;
		}
		try
		{
			FileInfo fileInfo = new FileInfo(System.IO.Path.Combine(sessionDir, fileName));
			return fileInfo.Exists && fileInfo.Length > 0;
		}
		catch
		{
			return false;
		}
	}
}
