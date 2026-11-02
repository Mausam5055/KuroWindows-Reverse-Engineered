using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using ClientTelemetry;
using KuroWindows.RecorderCore.Segments;
using KuroWindows.Storage;

namespace KuroWindows.Upload;

internal sealed class UploadSegmentDiscovery
{
	private readonly StorageManager _storage;

	private readonly Func<(string? Dir, int? Seg)> _getActiveSegment;

	private readonly Func<string, bool> _isInFlight;

	private readonly Action<string, string> _scheduleLocalReadinessRetry;

	private readonly Func<long> _opportunitySeconds;

	private readonly ConcurrentDictionary<string, byte> _onceKeys = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);

	private readonly ConcurrentDictionary<string, byte> _loggedPrioritySegments = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);

	private readonly ConcurrentDictionary<string, byte> _loggedRetryNotDue = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);

	private readonly ConcurrentDictionary<string, byte> _loggedDirFailures = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);

	private readonly ConcurrentDictionary<string, byte> _loggedLockedFiles = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);

	public UploadSegmentDiscovery(StorageManager storage, Func<(string? Dir, int? Seg)> getActiveSegment, Func<string, bool> isInFlight, Action<string, string> scheduleLocalReadinessRetry, Func<long> opportunitySeconds)
	{
		_storage = storage;
		_getActiveSegment = getActiveSegment;
		_isInFlight = isInFlight;
		_scheduleLocalReadinessRetry = scheduleLocalReadinessRetry;
		_opportunitySeconds = opportunitySeconds;
	}

	public IReadOnlyList<SegmentInfo> DiscoverPendingSegments()
	{
		List<SegmentInfo> list = new List<SegmentInfo>();
		var (activeDir, activeSeg) = _getActiveSegment();
		foreach (string item in _storage.EnumerateSessionDirectories().OrderBy(Path.GetFileName))
		{
			string fileName = Path.GetFileName(item);
			try
			{
				DiscoverSessionDirectory(item, fileName, activeDir, activeSeg, list);
			}
			catch (Exception ex)
			{
				if (_loggedDirFailures.TryAdd(fileName, 0))
				{
					Log($"  Discovery failed for session dir {fileName}; skipping it this cycle: {ex.GetType().Name}: {ex.Message}", "DiscoverPendingSegments", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Upload\\UploadSegmentDiscovery.cs", 63);
				}
				continue;
			}
			_loggedDirFailures.TryRemove(fileName, out var _);
		}
		list.Sort(CompareUploadPriority);
		return list;
	}

	internal void DiscoverSessionDirectory(string sessionDir, string sessionId, string? activeDir, int? activeSeg, List<SegmentInfo> result)
	{
		HashSet<int> source = CollectSegmentIndexes(sessionDir);
		DateTime utcNow = DateTime.UtcNow;
		TimeSpan backlogPurgeAge = UploadRetentionPolicy.BacklogPurgeAge;
		long opportunitySeconds = _opportunitySeconds();
		foreach (int item in source.OrderBy((int index) => index))
		{
			string text = SegmentKey(sessionId, item);
			if (_isInFlight(text))
			{
				continue;
			}
			if (activeDir != null && activeSeg.HasValue && string.Equals(sessionDir, activeDir, StringComparison.OrdinalIgnoreCase) && item == activeSeg.Value)
			{
				Log($"  Skipping {sessionId} seg={item} (currently recording)", "DiscoverSessionDirectory", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Upload\\UploadSegmentDiscovery.cs", 104);
				continue;
			}
			SegmentManifestLoadResult load = SegmentManifestStore.TryLoadDetailed(SegmentManifestStore.ManifestPath(sessionDir, item));
			SegmentManifest manifest = load.Manifest;
			UploadEligibility uploadEligibility = SegmentFate.UploadEligibilityFor(sessionDir, load, utcNow);
			bool flag = uploadEligibility.Kind == UploadEligibilityKind.Quarantine && uploadEligibility.Reason == QuarantineReason.FileMissing && SweepOwnsCandidate(sessionDir, item, backlogPurgeAge, opportunitySeconds);
			if (uploadEligibility.Kind == UploadEligibilityKind.Quarantine && !flag)
			{
				if (Once(text, UploadEligibilityKind.Quarantine))
				{
					SegmentFate.Quarantine(sessionDir, item, manifest, uploadEligibility.Reason.Value, FateOrigin.Discovery, uploadEligibility.Detail);
				}
				continue;
			}
			if (uploadEligibility.Kind != UploadEligibilityKind.Eligible)
			{
				UploadEligibilityKind verdict = (flag ? UploadEligibilityKind.Withheld : uploadEligibility.Kind);
				if (Once(text, verdict))
				{
					KuroLog.LocalOnly("upload", $"  Skipping {sessionId} seg={item}: segment manifest " + (flag ? "withheld; owner=sweep " : (VerdictLabel(verdict) + "; ")) + DescribeUploadEligibility(sessionDir, load), "DiscoverSessionDirectory", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Upload\\UploadSegmentDiscovery.cs", 139);
				}
				if (manifest != null && SegmentManifestStore.IsInterruptedState(manifest.State))
				{
					_scheduleLocalReadinessRetry(text, $"manifest {manifest.State}");
				}
				continue;
			}
			Forget(text, UploadEligibilityKind.Wait);
			Forget(text, UploadEligibilityKind.Withheld);
			if (!UploadManifestStore.IsRetryDue(sessionDir, item))
			{
				if (_loggedRetryNotDue.TryAdd(text, 0))
				{
					Log($"  Skipping {sessionId} seg={item}: durable retry time has not elapsed", "DiscoverSessionDirectory", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Upload\\UploadSegmentDiscovery.cs", 161);
				}
				continue;
			}
			_loggedRetryNotDue.TryRemove(text, out var _);
			List<DisplayFiles> list = BuildDisplayFiles(sessionDir, item, manifest, text, sessionId);
			if (list.Count > 0)
			{
				SegmentInfo segmentInfo = new SegmentInfo(sessionDir, sessionId, item, list, manifest?.State ?? SegmentManifestState.Complete, HasCaptureSurfaceStale(manifest), SegmentManifestStore.IsRecovered(manifest), manifest?.StartedWallMs ?? 0);
				if (UploadPriorityRank(segmentInfo) < 2 && _loggedPrioritySegments.TryAdd(text, 0))
				{
					Log($"  Priority upload candidate {sessionId} seg={item}: state={segmentInfo.ManifestState} stale={segmentInfo.CaptureSurfaceStale} recovered={segmentInfo.WasRecovered} reason={FirstCaptureSurfaceStaleReason(manifest) ?? "n/a"}", "DiscoverSessionDirectory", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Upload\\UploadSegmentDiscovery.cs", 183);
				}
				result.Add(segmentInfo);
			}
		}
	}

	private static HashSet<int> CollectSegmentIndexes(string sessionDir)
	{
		HashSet<int> hashSet = new HashSet<int>();
		foreach (string item in Directory.EnumerateFiles(sessionDir))
		{
			string fileName = Path.GetFileName(item);
			if (SegmentManifestStore.TryParseManifestSegmentIndex(fileName, out var segmentIndex) || SegmentManifestStore.TryParseClipDisplayIndex(fileName, out var _, out segmentIndex))
			{
				hashSet.Add(segmentIndex);
			}
		}
		return hashSet;
	}

	private List<DisplayFiles> BuildDisplayFiles(string sessionDir, int segmentIndex, SegmentManifest? segmentManifest, string key, string sessionId)
	{
		List<DisplayFiles> list = new List<DisplayFiles>();
		if (segmentManifest != null)
		{
			List<SegmentDisplayManifest> displays = segmentManifest.Displays;
			if (displays != null && displays.Count > 0)
			{
				{
					foreach (var (screenIndex, text, text2, captureFps) in from d in segmentManifest.Displays
						orderby d.DisplayIndex
						select (ScreenIndex: d.DisplayIndex, VideoPath: Path.Combine(sessionDir, d.VideoFile), EventsPath: Path.Combine(sessionDir, d.EventsFile), CaptureFps: Math.Max(d.CaptureFps, 1)))
					{
						if (!IsFileReady(text))
						{
							if (_loggedLockedFiles.TryAdd(key + ":" + Path.GetFileName(text), 0))
							{
								Log($"  Skipping {sessionId} seg={segmentIndex}: {Path.GetFileName(text)} is locked", "BuildDisplayFiles", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Upload\\UploadSegmentDiscovery.cs", 250);
							}
							_scheduleLocalReadinessRetry(key, Path.GetFileName(text) + " locked");
							return new List<DisplayFiles>();
						}
						if (_loggedLockedFiles.TryRemove(key + ":" + Path.GetFileName(text), out var value))
						{
							Log($"  {Path.GetFileName(text)} readable again for {sessionId} seg={segmentIndex}", "BuildDisplayFiles", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Upload\\UploadSegmentDiscovery.cs", 258);
						}
						if (!IsFileReady(text2))
						{
							if (_loggedLockedFiles.TryAdd(key + ":" + Path.GetFileName(text2), 0))
							{
								Log($"  Skipping {sessionId} seg={segmentIndex}: {Path.GetFileName(text2)} is locked", "BuildDisplayFiles", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Upload\\UploadSegmentDiscovery.cs", 265);
							}
							_scheduleLocalReadinessRetry(key, Path.GetFileName(text2) + " locked");
							return new List<DisplayFiles>();
						}
						if (_loggedLockedFiles.TryRemove(key + ":" + Path.GetFileName(text2), out value))
						{
							Log($"  {Path.GetFileName(text2)} readable again for {sessionId} seg={segmentIndex}", "BuildDisplayFiles", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Upload\\UploadSegmentDiscovery.cs", 273);
						}
						list.Add(new DisplayFiles(screenIndex, text, text2, captureFps));
					}
					return list;
				}
			}
		}
		return new List<DisplayFiles>();
	}

	private static bool IsFileReady(string path)
	{
		try
		{
			using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None))
			{
				return true;
			}
		}
		catch
		{
			return false;
		}
	}

	public void ForgetLockedFiles(string key)
	{
		string value = key + ":";
		foreach (string key2 in _loggedLockedFiles.Keys)
		{
			if (key2.StartsWith(value, StringComparison.OrdinalIgnoreCase))
			{
				_loggedLockedFiles.TryRemove(key2, out var _);
			}
		}
	}

	public void ForgetSegment(string key)
	{
		Forget(key, UploadEligibilityKind.Wait);
		Forget(key, UploadEligibilityKind.Withheld);
		Forget(key, UploadEligibilityKind.Quarantine);
		_loggedPrioritySegments.TryRemove(key, out var value);
		_loggedRetryNotDue.TryRemove(key, out value);
		ForgetLockedFiles(key);
	}

	private static string SegmentKey(string clientSessionId, int segmentIndex)
	{
		return $"{clientSessionId}:{segmentIndex}";
	}

	private bool Once(string key, UploadEligibilityKind verdict)
	{
		return _onceKeys.TryAdd(OnceKey(key, verdict), 0);
	}

	private void Forget(string key, UploadEligibilityKind verdict)
	{
		_onceKeys.TryRemove(OnceKey(key, verdict), out var _);
	}

	private static string OnceKey(string key, UploadEligibilityKind verdict)
	{
		return key + ":" + VerdictLabel(verdict);
	}

	private static string VerdictLabel(UploadEligibilityKind verdict)
	{
		return verdict.ToString().ToLowerInvariant();
	}

	private static bool SweepOwnsCandidate(string sessionDir, int segmentIndex, TimeSpan backlogPurgeAge, long opportunitySeconds)
	{
		long? num = UploadMarkerFiles.ReadOpportunityStamp(sessionDir, segmentIndex);
		if (num.HasValue)
		{
			long valueOrDefault = num.GetValueOrDefault();
			if (valueOrDefault <= opportunitySeconds)
			{
				return TimeSpan.FromSeconds(opportunitySeconds - valueOrDefault) >= backlogPurgeAge;
			}
		}
		return false;
	}

	internal static int CompareUploadPriority(SegmentInfo left, SegmentInfo right)
	{
		int num = UploadPriorityRank(left);
		int num2 = UploadPriorityRank(right);
		if (num != num2)
		{
			return num.CompareTo(num2);
		}
		if (num == 2)
		{
			int num3 = StartOrder(left.StartedWallMs).CompareTo(StartOrder(right.StartedWallMs));
			if (num3 != 0)
			{
				return num3;
			}
		}
		int num4 = ((num < 2) ? string.Compare(right.ClientSessionId, left.ClientSessionId, StringComparison.OrdinalIgnoreCase) : string.Compare(left.ClientSessionId, right.ClientSessionId, StringComparison.OrdinalIgnoreCase));
		if (num4 != 0)
		{
			return num4;
		}
		return left.SegmentIndex.CompareTo(right.SegmentIndex);
	}

	private static long StartOrder(long startedWallMs)
	{
		if (startedWallMs <= 0)
		{
			return long.MaxValue;
		}
		return startedWallMs;
	}

	private static int UploadPriorityRank(SegmentInfo segment)
	{
		if (segment.CaptureSurfaceStale || SegmentManifestStore.IsDiagnosticUploadState(segment.ManifestState))
		{
			return 0;
		}
		if (segment.WasRecovered)
		{
			return 1;
		}
		return 2;
	}

	private static bool HasCaptureSurfaceStale(SegmentManifest? manifest)
	{
		return manifest?.Displays.Any((SegmentDisplayManifest display) => display.CaptureSurfaceStale) ?? false;
	}

	private static string? FirstCaptureSurfaceStaleReason(SegmentManifest? manifest)
	{
		return manifest?.Displays.Select((SegmentDisplayManifest display) => display.CaptureSurfaceStaleReason).FirstOrDefault((string reason) => !string.IsNullOrWhiteSpace(reason));
	}

	private static string DescribeUploadEligibility(string sessionDir, SegmentManifestLoadResult load)
	{
		SegmentManifest manifest = load.Manifest;
		if (manifest == null)
		{
			Exception readError = load.ReadError;
			if (readError == null)
			{
				if (!load.Corrupt)
				{
					return "manifest=missing";
				}
				return "manifest=corrupt";
			}
			return "manifest=unreadable error=" + KuroLog.ExceptionSummary(readError);
		}
		string value = ((manifest.Displays.Count == 0) ? "displays=0" : string.Join(";", manifest.Displays.Select((SegmentDisplayManifest display) => $"display={display.DisplayIndex} video={DescribeFile(sessionDir, display.VideoFile)} events={DescribeFile(sessionDir, display.EventsFile)} sidecar={DescribeFile(sessionDir, display.FrameSidecarFile)} encoded_frames={display.EncodedFrameCount} sidecar_error={(string.IsNullOrWhiteSpace(display.SidecarIntegrityError) ? "none" : display.SidecarIntegrityError)}")));
		return $"state={manifest.State} duration_ms={manifest.ClaimedDurationMs?.ToString() ?? "null"} stale={HasCaptureSurfaceStale(manifest)} {value}";
	}

	private static string DescribeFile(string sessionDir, string? fileName)
	{
		if (string.IsNullOrWhiteSpace(fileName))
		{
			return "missing_name";
		}
		try
		{
			FileInfo fileInfo = new FileInfo(Path.Combine(sessionDir, fileName));
			return fileInfo.Exists ? $"{Path.GetFileName(fileName)}:{fileInfo.Length}b" : (Path.GetFileName(fileName) + ":missing");
		}
		catch (Exception ex)
		{
			return Path.GetFileName(fileName) + ":error:" + ex.Message;
		}
	}

	private static void Log(string msg, [CallerMemberName] string memberName = "", [CallerFilePath] string filePath = "", [CallerLineNumber] int lineNumber = 0)
	{
		KuroLog.Upload(msg, memberName, filePath, lineNumber);
	}
}
