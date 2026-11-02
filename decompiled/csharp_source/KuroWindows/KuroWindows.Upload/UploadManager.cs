using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using ClientTelemetry;
using KuroWindows.Auth;
using KuroWindows.RecorderCore.Segments;
using KuroWindows.Storage;

namespace KuroWindows.Upload;

public class UploadManager
{
	private sealed record UploadAttemptResult(SegmentInfo Segment, bool Uploaded, Exception? Error, SegmentUploadFacts Facts, long? OpportunitySince);

	private sealed class SegmentUploadFacts
	{
		private readonly long _startedTicks = Stopwatch.GetTimestamp();

		public int Attempts;

		public int Files;

		public long Bytes;

		public long? RequestMs;

		public long? PutMs;

		public long? CompleteMs;

		public bool Multipart;

		public long? DurableRetryDelayS;

		public string? ServerStatus;

		public long ElapsedMs => ElapsedMsSince(_startedTicks);
	}

	private readonly record struct SegmentFileUploadResult(bool AlreadyUploaded)
	{
		public static SegmentFileUploadResult Uploaded => new SegmentFileUploadResult(AlreadyUploaded: false);

		public static SegmentFileUploadResult AlreadyUploadedResult => new SegmentFileUploadResult(AlreadyUploaded: true);
	}

	private readonly record struct UploadUrlLookupResult(bool AlreadyUploaded, UploadUrlInfo? UrlInfo)
	{
		public UploadUrlInfo Url => UrlInfo ?? throw new InvalidOperationException("Upload URL is not available.");

		public static UploadUrlLookupResult AlreadyUploadedResult => new UploadUrlLookupResult(AlreadyUploaded: true, null);

		public static UploadUrlLookupResult Pending(UploadUrlInfo urlInfo)
		{
			return new UploadUrlLookupResult(AlreadyUploaded: false, urlInfo);
		}
	}

	private sealed class UploadBatchProgress
	{
		private readonly UploadManager _owner;

		private readonly int _totalSegments;

		private int _completedSegments;

		private readonly ConcurrentDictionary<string, long> _segmentTotals = new ConcurrentDictionary<string, long>(StringComparer.OrdinalIgnoreCase);

		private readonly ConcurrentDictionary<string, long> _fileUploaded = new ConcurrentDictionary<string, long>(StringComparer.OrdinalIgnoreCase);

		private long _totalBytes;

		private long _uploadedBytes;

		public UploadBatchProgress(UploadManager owner, IReadOnlyList<SegmentInfo> segments)
		{
			_owner = owner;
			_totalSegments = segments.Count;
			_totalBytes = EstimateInitialBytes(segments);
			_owner.ReportProgress(new UploadProgressInfo(IsUploading: true, _totalSegments, 0, null, null, null, _totalBytes, 0L));
		}

		public void ReportFile(SegmentInfo segment, string fileName)
		{
			string key = FileKey(segment, fileName);
			_owner.ReportProgress(new UploadProgressInfo(IsUploading: true, _totalSegments, Volatile.Read(in _completedSegments), segment.ClientSessionId, segment.SegmentIndex, fileName, Volatile.Read(in _totalBytes), Volatile.Read(in _uploadedBytes)));
			_fileUploaded.TryAdd(key, 0L);
		}

		public void MarkSegmentCompleted(SegmentInfo segment)
		{
			int completedSegments = Interlocked.Increment(ref _completedSegments);
			_owner.ReportProgress(new UploadProgressInfo(IsUploading: true, _totalSegments, completedSegments, segment.ClientSessionId, segment.SegmentIndex, null, Volatile.Read(in _totalBytes), Volatile.Read(in _uploadedBytes)));
		}

		public void Finish()
		{
			_owner.ReportProgress(new UploadProgressInfo(IsUploading: false, _totalSegments, Volatile.Read(in _completedSegments), null, null, null, Volatile.Read(in _totalBytes), Volatile.Read(in _uploadedBytes)));
		}

		public void RegisterFiles(SegmentInfo segment, IReadOnlyList<SegmentFile> files)
		{
			long num = files.Sum((SegmentFile f) => f.SizeBytes);
			long num2 = segment.Displays.Sum((DisplayFiles d) =>
			{
				long num4 = 0L;
				try
				{
					num4 += new FileInfo(d.VideoPath).Length;
				}
				catch
				{
				}
				try
				{
					num4 += new FileInfo(d.EventsPath).Length;
				}
				catch
				{
				}
				return num4;
			});
			long num3 = Math.Max(num - num2, 0L);
			if (num3 > 0)
			{
				Interlocked.Add(ref _totalBytes, num3);
			}
			foreach (SegmentFile file in files)
			{
				_segmentTotals[FileKey(segment, file.Name)] = file.SizeBytes;
			}
		}

		public void ReportUploadedBytes(SegmentInfo segment, string fileName, long uploadedBytes)
		{
			string key = FileKey(segment, fileName);
			_fileUploaded.TryGetValue(key, out var value);
			_fileUploaded.AddOrUpdate(key, uploadedBytes, (string _, long _) => uploadedBytes);
			long num = uploadedBytes - value;
			if (num != 0L)
			{
				Interlocked.Add(ref _uploadedBytes, num);
			}
			_owner.ReportProgress(new UploadProgressInfo(IsUploading: true, _totalSegments, Volatile.Read(in _completedSegments), segment.ClientSessionId, segment.SegmentIndex, fileName, Volatile.Read(in _totalBytes), Volatile.Read(in _uploadedBytes)));
		}

		public void CompleteFile(SegmentInfo segment, string fileName)
		{
			string key = FileKey(segment, fileName);
			if (_segmentTotals.TryGetValue(key, out var value))
			{
				ReportUploadedBytes(segment, fileName, value);
			}
		}

		private static long EstimateInitialBytes(IReadOnlyList<SegmentInfo> segments)
		{
			long num = 0L;
			foreach (SegmentInfo segment in segments)
			{
				foreach (DisplayFiles display in segment.Displays)
				{
					try
					{
						num += new FileInfo(display.VideoPath).Length;
					}
					catch
					{
					}
					try
					{
						num += new FileInfo(display.EventsPath).Length;
					}
					catch
					{
					}
				}
			}
			return num;
		}

		private static string FileKey(SegmentInfo segment, string fileName)
		{
			return $"{segment.ClientSessionId}:{segment.SegmentIndex}:{fileName}";
		}
	}

	internal sealed class SweepTally
	{
		public int BacklogPurged;

		public int QuarantineExpired;

		public int CorruptExpired;

		public int OrphanExpired;

		public int DebrisExpired;

		public int Failed;

		public int SkippedRecoverySessions;

		public int SkippedInFlightSegments;

		public long BacklogDurationMs;

		public long BacklogBytes;

		public TimeSpan OldestOpportunity;

		public readonly HashSet<string> BacklogSessions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		public readonly Dictionary<SegmentManifestState, int> BacklogStates = new Dictionary<SegmentManifestState, int>();

		public int Deleted => BacklogPurged + QuarantineExpired + CorruptExpired + OrphanExpired + DebrisExpired;
	}

	private const string AuthRetryKey = "__auth_token_refresh__";

	private const string DeviceRegistrationRetryKey = "__device_registration__";

	private readonly StorageManager _storage;

	private readonly AuthManager _auth;

	private readonly APIClient _api = new APIClient();

	private readonly Func<(string? Dir, int? Seg)> _getActiveSegment;

	private readonly SemaphoreSlim _trigger = new SemaphoreSlim(0, 1);

	private readonly SemaphoreSlim _cycleLock = new SemaphoreSlim(1, 1);

	private readonly SemaphoreSlim _fileUploadSlots = new SemaphoreSlim(3, 3);

	private readonly SemaphoreSlim _largeVideoUploadSlot = new SemaphoreSlim(1, 1);

	private readonly object _pendingLock = new object();

	private readonly ConcurrentDictionary<string, byte> _inFlightSegments = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);

	private readonly ConcurrentDictionary<string, SegmentInfo> _pendingCleanups = new ConcurrentDictionary<string, SegmentInfo>(StringComparer.OrdinalIgnoreCase);

	private readonly ConcurrentDictionary<string, int> _transientFailureCounts = new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);

	private readonly UploadRetryScheduler _retryScheduler;

	private readonly UploadSegmentDiscovery _segmentDiscovery;

	private readonly UploadAwakeRequest _awakeRequest = new UploadAwakeRequest();

	private readonly UploadOpportunityClock _opportunity;

	private CancellationTokenSource? _cts;

	private Task? _runLoopTask;

	private bool _pendingRecordingUploadRequested;

	private bool _tokenRefreshFailureLogged;

	private bool _deviceIdUnavailableLogged;

	private bool _networkUnavailableLogged;

	private const int MaxConcurrentSegmentWorkers = 3;

	private const int MaxConcurrentFileUploads = 3;

	private const int MaxConcurrentMultipartPartsPerFile = 2;

	private const int MaxUrlRefreshes = 2;

	internal const int MaxHealReactions = 3;

	private static readonly TimeSpan ProactivePresignedUrlRefreshWindow = TimeSpan.FromMinutes(10.0);

	private static readonly TimeSpan LocalReadinessRetryDelay = TimeSpan.FromSeconds(10.0);

	private static readonly TimeSpan MultipartProgressLogInterval = TimeSpan.FromSeconds(60.0);

	private static readonly TimeSpan RetentionSweepInterval = TimeSpan.FromMinutes(15.0);

	internal const int MaxDeletionsPerPass = 200;

	private DateTimeOffset _lastRetentionSweepAt = DateTimeOffset.MinValue;

	public event Action<UploadProgressInfo>? ProgressChanged;

	public UploadManager(StorageManager storage, AuthManager auth, Func<(string? Dir, int? Seg)> getActiveSegment)
	{
		_storage = storage;
		_auth = auth;
		_getActiveSegment = getActiveSegment;
		_retryScheduler = new UploadRetryScheduler(() => _auth.IsSignedIn, TriggerUpload);
		_opportunity = new UploadOpportunityClock(_storage.RecordingsDirectory(), UploadPossible);
		_segmentDiscovery = new UploadSegmentDiscovery(_storage, _getActiveSegment, IsHeld, ScheduleLocalReadinessRetry, () => _opportunity.Seconds);
	}

	internal bool UploadPossible()
	{
		if (_auth.IsSignedIn && NetworkInterface.GetIsNetworkAvailable())
		{
			return APIClient.PlatformUp;
		}
		return false;
	}

	private static void Log(string msg, [CallerMemberName] string memberName = "", [CallerFilePath] string filePath = "", [CallerLineNumber] int lineNumber = 0)
	{
		KuroLog.Upload(msg, memberName, filePath, lineNumber);
	}

	public void Start()
	{
		if (_cts != null)
		{
			Log("UploadManager Start ignored because it is already running", "Start", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Upload\\UploadManager.cs", 116);
			return;
		}
		Log("UploadManager starting", "Start", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Upload\\UploadManager.cs", 120);
		_cts = new CancellationTokenSource();
		_retryScheduler.Start();
		_auth.StateChanged += OnAuthStateChanged;
		NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
		_opportunity.Start();
		CancellationToken token = _cts.Token;
		_runLoopTask = Task.Run(() => RunLoopAsync(token));
		TriggerUpload();
		Log("UploadManager started", "Start", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Upload\\UploadManager.cs", 130);
	}

	public void Stop()
	{
		Log("UploadManager stopping", "Stop", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Upload\\UploadManager.cs", 135);
		_auth.StateChanged -= OnAuthStateChanged;
		NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
		_opportunity.Stop();
		CancellationTokenSource cts = _cts;
		Task runLoopTask = _runLoopTask;
		_cts = null;
		_runLoopTask = null;
		if (cts != null)
		{
			cts.Cancel();
			bool flag = true;
			try
			{
				flag = runLoopTask?.Wait(2000) ?? true;
			}
			catch
			{
			}
			if (!flag)
			{
				Log("Upload run loop wait timed out during stop; continuing shutdown", "Stop", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Upload\\UploadManager.cs", 153);
			}
			cts.Dispose();
		}
		_retryScheduler.Stop();
		_awakeRequest.Release("upload manager stopped");
		Log("UploadManager stopped", "Stop", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Upload\\UploadManager.cs", 160);
	}

	public async Task DrainAsync(TimeSpan timeout)
	{
		if (!_auth.IsSignedIn)
		{
			Log("DrainAsync: not signed in; skipping", "DrainAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Upload\\UploadManager.cs", 171);
			return;
		}
		using CancellationTokenSource cts = new CancellationTokenSource(timeout);
		try
		{
			Log("DrainAsync: running final upload cycle", "DrainAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Upload\\UploadManager.cs", 178);
			await RunSerializedUploadCycleAsync(cts.Token).ConfigureAwait(continueOnCapturedContext: false);
			Log("DrainAsync: complete", "DrainAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Upload\\UploadManager.cs", 180);
		}
		catch (OperationCanceledException)
		{
			Log("DrainAsync: timed out", "DrainAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Upload\\UploadManager.cs", 184);
		}
		catch (Exception ex2)
		{
			Log("DrainAsync: error: " + ex2.Message, "DrainAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Upload\\UploadManager.cs", 188);
		}
	}

	public void TriggerUpload()
	{
		SignalUpload();
	}

	private void SignalUpload()
	{
		lock (_pendingLock)
		{
			_pendingRecordingUploadRequested = true;
		}
		try
		{
			_trigger.Release();
		}
		catch (SemaphoreFullException)
		{
		}
	}

	private void OnAuthStateChanged(object? sender, AuthStateChangedEventArgs e)
	{
		if (e.State == KuroWindows.Auth.AuthState.SignedIn)
		{
			Log("Auth signed in; triggering backlog upload", "OnAuthStateChanged", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Upload\\UploadManager.cs", 213);
			TriggerUpload();
		}
		else
		{
			_awakeRequest.Release("auth state " + e.State.ToString().ToLowerInvariant());
		}
	}

	private void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e)
	{
		if (!e.IsAvailable)
		{
			if (!_networkUnavailableLogged)
			{
				_networkUnavailableLogged = true;
				Log("Network became unavailable; uploads will resume when connectivity returns", "OnNetworkAvailabilityChanged", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Upload\\UploadManager.cs", 229);
			}
			_awakeRequest.Release("network unavailable");
		}
		else
		{
			_networkUnavailableLogged = false;
			if (_auth.IsSignedIn)
			{
				Log("Network became available; triggering backlog upload", "OnNetworkAvailabilityChanged", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Upload\\UploadManager.cs", 242);
				TriggerUpload();
			}
		}
	}

	private async Task RunLoopAsync(CancellationToken ct)
	{
		while (!ct.IsCancellationRequested)
		{
			try
			{
				await _trigger.WaitAsync(ct).ConfigureAwait(continueOnCapturedContext: false);
				while (!ct.IsCancellationRequested && ConsumePendingUploadRequest())
				{
					await RunSerializedUploadCycleAsync(ct).ConfigureAwait(continueOnCapturedContext: false);
				}
			}
			catch (OperationCanceledException)
			{
				Log("Upload loop cancelled", "RunLoopAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Upload\\UploadManager.cs", 265);
				break;
			}
			catch (Exception value)
			{
				Log($"Upload loop error: {value}", "RunLoopAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Upload\\UploadManager.cs", 270);
			}
		}
	}

	private async Task RunSerializedUploadCycleAsync(CancellationToken ct)
	{
		await _cycleLock.WaitAsync(ct).ConfigureAwait(continueOnCapturedContext: false);
		try
		{
			await RunUploadCycleAsync(ct).ConfigureAwait(continueOnCapturedContext: false);
		}
		finally
		{
			_cycleLock.Release();
		}
	}

	private async Task RunUploadCycleAsync(CancellationToken ct)
	{
		RetryPendingCleanups();
		RunLocalRetentionSweep();
		if (!_auth.IsSignedIn)
		{
			Log("Not signed in; skipping", "RunUploadCycleAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Upload\\UploadManager.cs", 297);
			return;
		}
		if (string.IsNullOrWhiteSpace(_auth.DeviceId))
		{
			_awakeRequest.Release("registered device unavailable");
			if (!_deviceIdUnavailableLogged)
			{
				_deviceIdUnavailableLogged = true;
				Log("Registered device id is unavailable; skipping upload cycle", "RunUploadCycleAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Upload\\UploadManager.cs", 307);
			}
			ScheduleRetry("__device_registration__", TimeSpan.FromSeconds(AppConfig.BackgroundUploadRetryDelaysSeconds.FirstOrDefault(60)), "Device registration retry");
			return;
		}
		if (_deviceIdUnavailableLogged)
		{
			_deviceIdUnavailableLogged = false;
			Log("Registered device id available; resuming uploads", "RunUploadCycleAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Upload\\UploadManager.cs", 319);
		}
		string accessToken;
		try
		{
			accessToken = await _auth.GetValidTokenAsync().ConfigureAwait(continueOnCapturedContext: false);
		}
		catch (Exception ex)
		{
			_awakeRequest.Release("auth token refresh failed");
			if (!_tokenRefreshFailureLogged)
			{
				_tokenRefreshFailureLogged = true;
				Log("Token refresh failed: " + ex.Message, "RunUploadCycleAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Upload\\UploadManager.cs", 333);
			}
			ScheduleRetry("__auth_token_refresh__", TimeSpan.FromSeconds(AppConfig.BackgroundUploadRetryDelaysSeconds.FirstOrDefault(60)), "Auth token refresh retry");
			return;
		}
		if (_tokenRefreshFailureLogged)
		{
			_tokenRefreshFailureLogged = false;
			Log("Auth token refresh restored; uploads resuming", "RunUploadCycleAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Upload\\UploadManager.cs", 342);
		}
		_api.SetAccessToken(accessToken);
		try
		{
			await RunRecordingUploadCycleAsync(ct).ConfigureAwait(continueOnCapturedContext: false);
		}
		catch (Exception value)
		{
			Log($"Recording upload cycle failed: {value}", "RunUploadCycleAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Upload\\UploadManager.cs", 353);
		}
	}

	private async Task RunRecordingUploadCycleAsync(CancellationToken ct)
	{
		HashSet<string> attemptedThisInvocation = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		_003C_003Ec__DisplayClass48_1 CS_0024_003C_003E8__locals21 = default;
		while (!ct.IsCancellationRequested)
		{
			List<SegmentInfo> list = (from segment in _segmentDiscovery.DiscoverPendingSegments()
				where !attemptedThisInvocation.Contains(SegmentKey(segment.ClientSessionId, segment.SegmentIndex))
				select segment).ToList();
			if (list.Count > 0)
			{
				Log($"Discovered {list.Count} pending segment(s)", "RunRecordingUploadCycleAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Upload\\UploadManager.cs", 368);
			}
			if (list.Count == 0)
			{
				_awakeRequest.Release("recording upload backlog drained");
				ReportProgress(new UploadProgressInfo(IsUploading: false, 0, 0, null, null, null, 0L, 0L));
				break;
			}
			_awakeRequest.Acquire($"pending_segments={list.Count}");
			bool uploadedAny = false;
			bool stopCycle = false;
			bool scheduledRetry = false;
			CS_0024_003C_003E8__locals21.progress = new UploadBatchProgress(this, list);
			CS_0024_003C_003E8__locals21.batchCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
			try
			{
				CS_0024_003C_003E8__locals21.pending = new Queue<SegmentInfo>(list);
				CS_0024_003C_003E8__locals21.running = new List<Task<UploadAttemptResult>>();
				EnqueueNext();
				while (CS_0024_003C_003E8__locals21.running.Count > 0)
				{
					ct.ThrowIfCancellationRequested();
					Task<UploadAttemptResult> task = await Task.WhenAny(CS_0024_003C_003E8__locals21.running).ConfigureAwait(continueOnCapturedContext: false);
					CS_0024_003C_003E8__locals21.running.Remove(task);
					UploadAttemptResult uploadAttemptResult = await task.ConfigureAwait(continueOnCapturedContext: false);
					string text = SegmentKey(uploadAttemptResult.Segment.ClientSessionId, uploadAttemptResult.Segment.SegmentIndex);
					if (uploadAttemptResult.Uploaded)
					{
						uploadedAny = true;
						CS_0024_003C_003E8__locals21.progress.MarkSegmentCompleted(uploadAttemptResult.Segment);
						_transientFailureCounts.TryRemove(text, out var _);
						SegmentUploadFacts facts = uploadAttemptResult.Facts;
						Telemetry.Emit(Ev.UploadFinished(uploadAttemptResult.Segment.ClientSessionId, uploadAttemptResult.Segment.SegmentIndex, facts.Attempts, facts.Files, facts.Bytes, facts.ElapsedMs, facts.RequestMs, facts.PutMs, facts.CompleteMs, facts.Multipart, facts.ServerStatus));
						_retryScheduler.Forget(text);
						_segmentDiscovery.ForgetSegment(text);
						long? opportunitySince = uploadAttemptResult.OpportunitySince;
						if (opportunitySince.HasValue)
						{
							long valueOrDefault = opportunitySince.GetValueOrDefault();
							if (_opportunity.Seconds >= valueOrDefault)
							{
								Observe.UploadLatencyS(_opportunity.Seconds - valueOrDefault);
							}
						}
						Log($"Segment terminal status=completed session={uploadAttemptResult.Segment.ClientSessionId} seg={uploadAttemptResult.Segment.SegmentIndex}", "RunRecordingUploadCycleAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Upload\\UploadManager.cs", 428);
						Count.SegmentUploadOk(1L);
					}
					else if (uploadAttemptResult.Error is OperationCanceledException && CS_0024_003C_003E8__locals21.batchCts.IsCancellationRequested)
					{
						Log($"Segment terminal status=deferred session={uploadAttemptResult.Segment.ClientSessionId} seg={uploadAttemptResult.Segment.SegmentIndex} reason={(ct.IsCancellationRequested ? "cycle_cancelled" : "batch_cancelled")}", "RunRecordingUploadCycleAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Upload\\UploadManager.cs", 435);
					}
					else if (uploadAttemptResult.Error != null)
					{
						if (HandleBlockingAuthFailure(uploadAttemptResult.Error))
						{
							Log($"Segment terminal status=blocked session={uploadAttemptResult.Segment.ClientSessionId} seg={uploadAttemptResult.Segment.SegmentIndex} error={uploadAttemptResult.Error.Message}", "RunRecordingUploadCycleAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Upload\\UploadManager.cs", 442);
							Telemetry.Emit(Ev.UploadBlocked(TelemetryMap.BlockReasonFor(uploadAttemptResult.Error), uploadAttemptResult.Segment.ClientSessionId, uploadAttemptResult.Segment.SegmentIndex, TelemetryMap.HttpStatusOf(uploadAttemptResult.Error)));
							stopCycle = true;
							CS_0024_003C_003E8__locals21.batchCts.Cancel();
							continue;
						}
						if (uploadAttemptResult.Error is SegmentFateRecordedException fate)
						{
							CloseFatedSegment(text, fate);
						}
						else
						{
							UploadFailure uploadFailure = UploadFailurePolicy.Classify(uploadAttemptResult.Error);
							long num = uploadAttemptResult.Facts.DurableRetryDelayS ?? TransientRetryDelaySeconds(text);
							Log($"Segment terminal status=retrying session={uploadAttemptResult.Segment.ClientSessionId} seg={uploadAttemptResult.Segment.SegmentIndex} attempt={uploadAttemptResult.Facts.Attempts} error={uploadAttemptResult.Error.Message}", "RunRecordingUploadCycleAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Upload\\UploadManager.cs", 460);
							Count.SegmentUploadFail(TelemetryMap.UploadFailReasonFor(uploadFailure.ErrorKind), 1L);
							Telemetry.Emit(Ev.UploadFailed(uploadAttemptResult.Segment.ClientSessionId, uploadAttemptResult.Segment.SegmentIndex, uploadAttemptResult.Facts.Attempts, uploadFailure.Phase, uploadFailure.ErrorKind, uploadFailure.HttpStatus, uploadFailure.ErrorCode, uploadAttemptResult.Error.Message, num));
							scheduledRetry = true;
							ScheduleRetry(text, TimeSpan.FromSeconds(num), "Retry for " + text);
						}
					}
					if (!stopCycle && !ct.IsCancellationRequested)
					{
						EnqueueNext();
					}
				}
				if (stopCycle)
				{
					_awakeRequest.Release("upload cycle blocked");
					CS_0024_003C_003E8__locals21.progress.Finish();
					break;
				}
				if (!uploadedAny)
				{
					_awakeRequest.Release(scheduledRetry ? "upload retry backoff" : "no upload progress");
					CS_0024_003C_003E8__locals21.progress.Finish();
					break;
				}
				CS_0024_003C_003E8__locals21.progress.Finish();
			}
			finally
			{
				if (CS_0024_003C_003E8__locals21.batchCts != null)
				{
					((IDisposable)CS_0024_003C_003E8__locals21.batchCts).Dispose();
				}
			}
			CS_0024_003C_003E8__locals21 = default;
			void EnqueueNext()
			{
				while (CS_0024_003C_003E8__locals21.running.Count < 3 && CS_0024_003C_003E8__locals21.pending.Count > 0)
				{
					SegmentInfo segmentInfo = CS_0024_003C_003E8__locals21.pending.Dequeue();
					attemptedThisInvocation.Add(SegmentKey(segmentInfo.ClientSessionId, segmentInfo.SegmentIndex));
					Log($"Uploading {segmentInfo.ClientSessionId} seg={segmentInfo.SegmentIndex} displays={segmentInfo.Displays.Count} state={segmentInfo.ManifestState} stale={segmentInfo.CaptureSurfaceStale}", "RunRecordingUploadCycleAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Upload\\UploadManager.cs", 392);
					CS_0024_003C_003E8__locals21.running.Add(StartUploadTask(segmentInfo, CS_0024_003C_003E8__locals21.batchCts.Token, CS_0024_003C_003E8__locals21.progress));
				}
			}
		}
	}

	private Task<UploadAttemptResult> StartUploadTask(SegmentInfo segment, CancellationToken ct, UploadBatchProgress progress)
	{
		string key = SegmentKey(segment.ClientSessionId, segment.SegmentIndex);
		_inFlightSegments[key] = 0;
		long? opportunitySince = UploadMarkerFiles.ReadOpportunityStamp(segment.SessionDir, segment.SegmentIndex);
		return Task.Run(async () =>
		{
			using (Telemetry.Scope(segment.ClientSessionId, segment.SegmentIndex))
			{
				SegmentUploadFacts facts = new SegmentUploadFacts();
				try
				{
					await UploadSegmentAsync(segment, facts, ct, progress).ConfigureAwait(continueOnCapturedContext: false);
					return new UploadAttemptResult(segment, Uploaded: true, null, facts, opportunitySince);
				}
				catch (Exception error)
				{
					return new UploadAttemptResult(segment, Uploaded: false, error, facts, opportunitySince);
				}
				finally
				{
					_inFlightSegments.TryRemove(key, out var _);
				}
			}
		});
	}

	private static long ElapsedMsSince(long startTicks)
	{
		return (long)Stopwatch.GetElapsedTime(startTicks).TotalMilliseconds;
	}

	private static bool HasMultipartFile(UploadManifest manifest)
	{
		return manifest.Files.Any((UploadFileManifest f) => string.Equals(f.Mode, "multipart", StringComparison.OrdinalIgnoreCase));
	}

	private bool HandleBlockingAuthFailure(Exception ex)
	{
		if (!(ex is APIClientException ex2))
		{
			return false;
		}
		if (ex2.IsSessionExpired)
		{
			Log("Upload aborted because auth/session expired; signing out", "HandleBlockingAuthFailure", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Upload\\UploadManager.cs", 712);
			Telemetry.Emit(Ev.AuthForcedSignout(SignoutTrigger.Upload401, ex2.StatusCode));
			_auth.SignOutAsync();
			return true;
		}
		if (ex2.IsUpdateRequired)
		{
			Log("Upload aborted because backend requires an update", "HandleBlockingAuthFailure", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Upload\\UploadManager.cs", 721);
			return true;
		}
		if (ex2.IsDeviceOrProfileBlocked)
		{
			Log("Upload blocked by server: " + (ex2.Code ?? ex2.ResponseBody) + "; preserving local files", "HandleBlockingAuthFailure", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Upload\\UploadManager.cs", 727);
			return true;
		}
		return false;
	}

	private void ScheduleLocalReadinessRetry(string key, string reason)
	{
		ScheduleRetry(key, LocalReadinessRetryDelay, $"Local readiness retry for {key} ({reason})");
	}

	private void ScheduleRetry(string key, TimeSpan delay, string logPrefix)
	{
		_retryScheduler.Schedule(key, delay, logPrefix);
	}

	private async Task UploadSegmentAsync(SegmentInfo segment, SegmentUploadFacts facts, CancellationToken ct, UploadBatchProgress progress)
	{
		UploadManifestStore uploadManifestStore = new UploadManifestStore(segment.SessionDir, segment.SegmentIndex);
		UploadManifest uploadManifest = uploadManifestStore.LoadOrCreate();
		SegmentMetrics metrics;
		UploadFileDescriptor[] fileDescs;
		try
		{
			metrics = await MetadataBuilder.BuildAsync(segment, AuthManager.GetOsVersion(), AuthManager.GetMachineModel()).ConfigureAwait(continueOnCapturedContext: false);
			progress.RegisterFiles(segment, metrics.AllFiles);
			fileDescs = metrics.AllFiles.Select((SegmentFile f) => DescribeUploadFile(segment.SessionDir, f)).ToArray();
		}
		catch (Exception ex)
		{
			string key = SegmentKey(segment.ClientSessionId, segment.SegmentIndex);
			if (_transientFailureCounts.ContainsKey(key) || UploadMarkerFiles.ReadAttemptCount(segment.SessionDir, segment.SegmentIndex) > 0)
			{
				KuroLog.LocalOnly("upload", $"  UploadSegmentAsync error before the request: {ex}", "UploadSegmentAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Upload\\UploadManager.cs", 773);
			}
			else
			{
				Log($"  UploadSegmentAsync error before the request: {ex}", "UploadSegmentAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Upload\\UploadManager.cs", 777);
			}
			facts.DurableRetryDelayS = MarkUploadFailure(segment, uploadManifestStore, uploadManifest, UploadFailurePolicy.Classify(ex), ex.Message);
			throw;
		}
		Log($"  duration={metrics.DurationMs}ms idle={metrics.IdleMs}ms files={metrics.AllFiles.Count}", "UploadSegmentAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Upload\\UploadManager.cs", 785);
		uploadManifest.State = UploadManifestState.Ready;
		uploadManifest.SessionId = segment.ClientSessionId;
		uploadManifest.SegmentIndex = segment.SegmentIndex;
		uploadManifest.Attempts++;
		uploadManifest.LastAttemptWallMs = UploadManifestStore.NowMs();
		uploadManifest.NextRetryWallMs = null;
		uploadManifest.LastErrorCode = null;
		uploadManifest.LastErrorMessage = null;
		SyncUploadManifestFiles(uploadManifest, fileDescs);
		uploadManifestStore.Save(uploadManifest);
		long expectedTotalBytes = metrics.AllFiles.Sum((SegmentFile f) => f.SizeBytes);
		List<string> uploadFileNames = metrics.AllFiles.Select((SegmentFile f) => f.Name).ToList();
		facts.Attempts = uploadManifest.Attempts;
		facts.Files = metrics.AllFiles.Count;
		facts.Bytes = expectedTotalBytes;
		UploadRequestResponse uploadResp = null;
		Dictionary<string, string> alreadyUploadedFileHashes = null;
		int urlRefreshCount = 0;
		object uploadManifestLock = new object();
		SemaphoreSlim urlRefreshLock = new SemaphoreSlim(1, 1);
		try
		{
			int num;
			_ = num - 1;
			_ = 6;
			try
			{
				await CompleteRecoverableMultipartUploadsAsync(segment, metrics.AllFiles, uploadManifestStore, uploadManifest, progress, ct).ConfigureAwait(continueOnCapturedContext: false);
				if (await TryCompleteAlreadyUploadedSegmentAsync().ConfigureAwait(continueOnCapturedContext: false))
				{
					return;
				}
				SetUploadState(uploadManifestStore, uploadManifest, UploadManifestState.RequestingUrls);
				APIClient api = _api;
				api.SetAccessToken(await _auth.GetValidTokenAsync().ConfigureAwait(continueOnCapturedContext: false));
				long requestStarted = Stopwatch.GetTimestamp();
				uploadResp = await _api.RequestUploadAsync(segment.ClientSessionId, segment.SegmentIndex, metrics.DurationMs, metrics.IdleMs, metrics.CapturedAt ?? DateTimeOffset.UtcNow, fileDescs, _auth.DeviceId, ct).ConfigureAwait(continueOnCapturedContext: false);
				facts.RequestMs = ElapsedMsSince(requestStarted);
				uploadManifest.UrlsIssuedWallMs = UploadManifestStore.NowMs();
				uploadManifestStore.Save(uploadManifest);
				int value = Enumerable.Count(uploadResp.UploadUrls.Values, (UploadUrlInfo url) => string.Equals(url.Mode, "multipart", StringComparison.OrdinalIgnoreCase));
				Log($"  upload-request status={uploadResp.Status} urls={uploadResp.UploadUrls.Count} multipart_files={value} expires_in_seconds={uploadResp.ExpiresIn?.ToString() ?? "-"}", "UploadSegmentAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Upload\\UploadManager.cs", 840);
				switch (uploadResp.Status.ToLowerInvariant())
				{
				case "already_uploaded":
					ResolveAlreadyUploaded(uploadResp.FileHashes, "upload-request");
					break;
				case "session_expired":
					throw new APIClientException("desktop-v2-upload-request", 401, uploadResp.Status);
				case "update_required":
					throw APIClient.UpdateRequiredByStatus("desktop-v2-upload-request", uploadResp.Status);
				default:
					throw new InvalidOperationException($"Unhandled upload-request status '{uploadResp.Status}' for {segment.ClientSessionId} seg={segment.SegmentIndex}.");
				case "pending":
				{
					SetUploadState(uploadManifestStore, uploadManifest, UploadManifestState.Uploading);
					using (CancellationTokenSource segmentUploadCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
					{
						long putStarted = Stopwatch.GetTimestamp();
						try
						{
							foreach (IReadOnlyList<SegmentFile> item in UploadPhases(metrics.AllFiles))
							{
								if (item.Count != 0 && (await UploadPhaseAsync(item, segmentUploadCts, segmentUploadCts.Token).ConfigureAwait(continueOnCapturedContext: false)).AlreadyUploaded)
								{
									facts.PutMs = ElapsedMsSince(putStarted);
									facts.Multipart = HasMultipartFile(uploadManifest);
									ResolveAlreadyUploaded(alreadyUploadedFileHashes, "mid-upload refresh");
									return;
								}
							}
						}
						catch
						{
							segmentUploadCts.Cancel();
							throw;
						}
						facts.PutMs = ElapsedMsSince(putStarted);
						facts.Multipart = HasMultipartFile(uploadManifest);
						SetUploadState(uploadManifestStore, uploadManifest, UploadManifestState.Completing);
						api = _api;
						api.SetAccessToken(await _auth.GetValidTokenAsync().ConfigureAwait(continueOnCapturedContext: false));
						long completeStarted = Stopwatch.GetTimestamp();
						UploadCompleteResponse uploadCompleteResponse = await CompleteUploadWithRetryAsync(segment.ClientSessionId, segment.SegmentIndex, expectedTotalBytes, uploadFileNames, metrics.DurationMs, ct).ConfigureAwait(continueOnCapturedContext: false);
						facts.CompleteMs = ElapsedMsSince(completeStarted);
						facts.ServerStatus = uploadCompleteResponse.Status;
						HandleUploadCompleteResponse(uploadCompleteResponse, segment, uploadManifestStore, uploadManifest);
					}
					break;
				}
				}
			}
			catch (SegmentFateRecordedException)
			{
				throw;
			}
			catch (Exception ex3)
			{
				string key2 = SegmentKey(segment.ClientSessionId, segment.SegmentIndex);
				if (_transientFailureCounts.ContainsKey(key2) || UploadMarkerFiles.ReadAttemptCount(segment.SessionDir, segment.SegmentIndex) > 0)
				{
					KuroLog.LocalOnly("upload", $"  UploadSegmentAsync error: {ex3}", "UploadSegmentAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Upload\\UploadManager.cs", 931);
				}
				else
				{
					Log($"  UploadSegmentAsync error: {ex3}", "UploadSegmentAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Upload\\UploadManager.cs", 935);
				}
				UploadFailure uploadFailure = UploadFailurePolicy.Classify(ex3);
				string healCode = uploadFailure.HealCode;
				if (healCode != null)
				{
					int num2 = UploadMarkerFiles.IncrementHealCount(segment.SessionDir, segment.SegmentIndex);
					UploadReaction? uploadReaction = UploadFailurePolicy.HealReaction(healCode);
					if (uploadReaction.HasValue)
					{
						UploadReaction valueOrDefault = uploadReaction.GetValueOrDefault();
						Count.UploadReaction(valueOrDefault, 1L);
					}
					if (num2 > 3)
					{
						Count.UploadReaction(UploadReaction.HealEscalated, 1L);
						throw QuarantineHealEscalated(segment, uploadManifestStore, uploadManifest, healCode, num2, uploadFailure.DurableErrorCode, ex3);
					}
					if (uploadFailure.HealScope == HealScope.File)
					{
						string healFile = uploadFailure.HealFile;
						if (healFile != null)
						{
							lock (uploadManifestLock)
							{
								ResetMultipartFileState(uploadManifestStore, uploadManifest, healFile);
							}
							Log($"  multipart-complete could not verify file={healFile} code={healCode}; resetting its multipart state for safe re-upload", "UploadSegmentAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Upload\\UploadManager.cs", 956);
							goto IL_11ea;
						}
					}
					Log("  upload-complete could not verify uploaded objects; clearing durable file markers for safe re-upload", "UploadSegmentAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Upload\\UploadManager.cs", 963);
					ClearUploadedFileMarkers(uploadManifestStore, uploadManifest);
				}
				goto IL_11ea;
				IL_11ea:
				facts.DurableRetryDelayS = MarkUploadFailure(segment, uploadManifestStore, uploadManifest, uploadFailure, ex3.Message);
				throw;
			}
		}
		finally
		{
			if (urlRefreshLock != null)
			{
				((IDisposable)urlRefreshLock).Dispose();
			}
		}
		static async Task DrainRemainingFileTasksAsync(IReadOnlyList<Task<SegmentFileUploadResult>> fileTasks)
		{
			if (fileTasks.Count == 0)
			{
				return;
			}
			try
			{
				await Task.WhenAll(fileTasks).ConfigureAwait(continueOnCapturedContext: false);
			}
			catch
			{
			}
		}
		async Task<UploadUrlLookupResult> EnsureFreshUploadUrlAsync(string fileName, Exception? refreshCause, CancellationToken fileCt)
		{
			await urlRefreshLock.WaitAsync(fileCt).ConfigureAwait(continueOnCapturedContext: false);
			try
			{
				UploadRequestResponse uploadRequestResponse = uploadResp ?? throw new InvalidOperationException("Upload URLs have not been issued yet.");
				if (refreshCause != null || ShouldRefreshUploadUrlsSoon(uploadRequestResponse, uploadManifest.UrlsIssuedWallMs))
				{
					if (refreshCause is S3UploadException { Failure: S3UploadFailure.DigestMismatch } ex4)
					{
						int num3 = Array.FindIndex(fileDescs, (UploadFileDescriptor d) => string.Equals(d.Name, fileName, StringComparison.OrdinalIgnoreCase));
						string? sha = fileDescs[num3].Sha256;
						UploadFileDigests.Invalidate(Path.Combine(segment.SessionDir, fileName));
						fileDescs[num3] = DescribeUploadFile(segment.SessionDir, metrics.AllFiles[num3]);
						bool flag = !string.Equals(sha, fileDescs[num3].Sha256, StringComparison.OrdinalIgnoreCase);
						Log($"  Re-hashed {fileName} after digest rejection; changed={flag}", "UploadSegmentAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Upload\\UploadManager.cs", 1150);
						Count.UploadReaction(UploadReaction.DigestRehash, 1L);
						Telemetry.Emit(Ev.UploadIntegrityRejected(segment.ClientSessionId, segment.SegmentIndex, fileName, UploadFailurePolicy.Classify(ex4).Phase, uploadManifest.Attempts, (long)ex4.StatusCode, flag, ex4.Message));
						if (!flag)
						{
							Log("  Digest rejection of " + fileName + " survived the re-hash; escalating without a refresh", "UploadSegmentAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Upload\\UploadManager.cs", 1158);
							throw new UploadDigestRejectedException(fileName, ex4);
						}
					}
					if (urlRefreshCount >= 2)
					{
						if (refreshCause != null)
						{
							Log("  Presigned URL refresh limit reached for " + fileName, "UploadSegmentAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Upload\\UploadManager.cs", 1169);
							ExceptionDispatchInfo.Capture(refreshCause).Throw();
						}
						if (!uploadRequestResponse.UploadUrls.TryGetValue(fileName, out UploadUrlInfo value2))
						{
							throw new InvalidOperationException("Missing presigned URL for " + fileName + ".");
						}
						return UploadUrlLookupResult.Pending(value2);
					}
					urlRefreshCount++;
					Log($"  Refreshing upload URLs ({urlRefreshCount}/{2}) before {fileName}", "UploadSegmentAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Upload\\UploadManager.cs", 1181);
					UploadRequestResponse uploadRequestResponse2 = await RefreshUploadUrlsAsync(segment, fileDescs, metrics.DurationMs, metrics.IdleMs, metrics.CapturedAt ?? DateTimeOffset.UtcNow, fileCt).ConfigureAwait(continueOnCapturedContext: false);
					if (uploadRequestResponse2.Status.Equals("already_uploaded", StringComparison.OrdinalIgnoreCase))
					{
						alreadyUploadedFileHashes = uploadRequestResponse2.FileHashes;
						return UploadUrlLookupResult.AlreadyUploadedResult;
					}
					uploadResp = uploadRequestResponse2;
					uploadRequestResponse = uploadRequestResponse2;
					uploadManifest.UrlsIssuedWallMs = UploadManifestStore.NowMs();
					uploadManifestStore.Save(uploadManifest);
				}
				if (!uploadRequestResponse.UploadUrls.TryGetValue(fileName, out UploadUrlInfo value3))
				{
					throw new InvalidOperationException("Missing presigned URL for " + fileName + ".");
				}
				return UploadUrlLookupResult.Pending(value3);
			}
			finally
			{
				urlRefreshLock.Release();
			}
		}
		void ResolveAlreadyUploaded(IReadOnlyDictionary<string, string>? serverFileHashes, string context)
		{
			AlreadyUploadedOutcome alreadyUploadedOutcome = AlreadyUploadedOutcomeFor(serverFileHashes, fileDescs);
			if (alreadyUploadedOutcome == AlreadyUploadedOutcome.Collision)
			{
				throw RecordAlreadyUploadedCollision(segment, uploadManifestStore, uploadManifest, context);
			}
			Count.AlreadyUploaded(alreadyUploadedOutcome, 1L);
			facts.ServerStatus = "already_uploaded";
			Log("  Segment already uploaded according to server; cleaning up local files (" + context + ") outcome=" + alreadyUploadedOutcome.ToWire(), "UploadSegmentAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Upload\\UploadManager.cs", 987);
			SetUploadState(uploadManifestStore, uploadManifest, UploadManifestState.Complete);
			CleanupUploadedOrDefer(segment);
		}
		async Task<bool> TryCompleteAlreadyUploadedSegmentAsync()
		{
			if (!UploadManifestHasAllFilesUploaded(uploadManifest, fileDescs))
			{
				return false;
			}
			Log("  Durable upload manifest says all files are uploaded; retrying desktop-v2-upload-complete before requesting new URLs", "UploadSegmentAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Upload\\UploadManager.cs", 999);
			try
			{
				SetUploadState(uploadManifestStore, uploadManifest, UploadManifestState.Completing);
				APIClient api2 = _api;
				api2.SetAccessToken(await _auth.GetValidTokenAsync().ConfigureAwait(continueOnCapturedContext: false));
				long completeStarted2 = Stopwatch.GetTimestamp();
				UploadCompleteResponse uploadCompleteResponse2 = await CompleteUploadWithRetryAsync(segment.ClientSessionId, segment.SegmentIndex, expectedTotalBytes, uploadFileNames, metrics.DurationMs, ct).ConfigureAwait(continueOnCapturedContext: false);
				facts.CompleteMs = ElapsedMsSince(completeStarted2);
				facts.Multipart = HasMultipartFile(uploadManifest);
				facts.ServerStatus = uploadCompleteResponse2.Status;
				HandleUploadCompleteResponse(uploadCompleteResponse2, segment, uploadManifestStore, uploadManifest);
				return true;
			}
			catch (APIClientException ex4) when (string.Equals(ex4.Operation, "desktop-v2-upload-complete", StringComparison.OrdinalIgnoreCase) && string.Equals(ex4.Code, "upload_request_not_found", StringComparison.OrdinalIgnoreCase))
			{
				Log("  Server has no desktop-v2-upload-request row for uploaded local files; requesting fresh URLs before retrying", "UploadSegmentAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Upload\\UploadManager.cs", 1027);
				return false;
			}
		}
		async Task<SegmentFileUploadResult> UploadPhaseAsync(IReadOnlyList<SegmentFile> phase, CancellationTokenSource phaseCts, CancellationToken phaseCt)
		{
			List<Task<SegmentFileUploadResult>> fileTasks = phase.Select((SegmentFile file) => UploadSegmentFileAsync(file, phaseCt)).ToList();
			try
			{
				while (fileTasks.Count > 0)
				{
					Task<SegmentFileUploadResult> task = await Task.WhenAny(fileTasks).ConfigureAwait(continueOnCapturedContext: false);
					fileTasks.Remove(task);
					if ((await task.ConfigureAwait(continueOnCapturedContext: false)).AlreadyUploaded)
					{
						phaseCts.Cancel();
						await DrainRemainingFileTasksAsync(fileTasks).ConfigureAwait(continueOnCapturedContext: false);
						return SegmentFileUploadResult.AlreadyUploadedResult;
					}
				}
				return SegmentFileUploadResult.Uploaded;
			}
			catch
			{
				phaseCts.Cancel();
				await DrainRemainingFileTasksAsync(fileTasks).ConfigureAwait(continueOnCapturedContext: false);
				throw;
			}
		}
		async Task<SegmentFileUploadResult> UploadSegmentFileAsync(SegmentFile file, CancellationToken fileCt)
		{
			string name = file.Name;
			string fullPath = Path.Combine(segment.SessionDir, name);
			progress.ReportFile(segment, name);
			lock (uploadManifestLock)
			{
				if (uploadManifest.Files.FirstOrDefault((UploadFileManifest f) => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase))?.Uploaded ?? false)
				{
					progress.CompleteFile(segment, name);
					Log("  Skipping " + name + "; durable upload manifest marks it uploaded", "UploadSegmentAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Upload\\UploadManager.cs", 1045);
					return SegmentFileUploadResult.Uploaded;
				}
			}
			while (true)
			{
				fileCt.ThrowIfCancellationRequested();
				UploadUrlLookupResult uploadUrlLookupResult = await EnsureFreshUploadUrlAsync(name, null, fileCt).ConfigureAwait(continueOnCapturedContext: false);
				if (uploadUrlLookupResult.AlreadyUploaded)
				{
					break;
				}
				UploadUrlInfo url = uploadUrlLookupResult.Url;
				string value2 = (string.Equals(url.Mode, "multipart", StringComparison.OrdinalIgnoreCase) ? $"multipart parts={url.Parts?.Count ?? 0}" : "single");
				Log($"  Uploading {name} ({file.SizeBytes} bytes) mode={value2}...", "UploadSegmentAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Upload\\UploadManager.cs", 1062);
				try
				{
					if (!string.Equals(url.Mode, "multipart", StringComparison.OrdinalIgnoreCase))
					{
						await UploadFileWithRetryAsync(segment, name, fullPath, file.SizeBytes, url.Url, url.ContentType ?? UploadFileKinds.ContentTypeFor(name), url.Headers, progress, fileCt).ConfigureAwait(continueOnCapturedContext: false);
					}
					else
					{
						await UploadMultipartFileAsync(segment, file, fullPath, url, uploadManifestStore, uploadManifest, uploadManifestLock, progress, fileCt).ConfigureAwait(continueOnCapturedContext: false);
					}
					lock (uploadManifestLock)
					{
						MarkUploadFile(uploadManifestStore, uploadManifest, name, fileDescs.First((UploadFileDescriptor d) => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase)).Sha256, uploaded: true);
					}
					progress.CompleteFile(segment, name);
					Log("  Uploaded " + name + " OK", "UploadSegmentAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Upload\\UploadManager.cs", 1099);
					return SegmentFileUploadResult.Uploaded;
				}
				catch (Exception ex4) when (UploadFailurePolicy.Classify(ex4).NeedsPresign)
				{
					UploadFailure uploadFailure2 = UploadFailurePolicy.Classify(ex4);
					Log("  S3 rejected " + name + "; refreshing URLs: " + ex4.Message, "UploadSegmentAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Upload\\UploadManager.cs", 1105);
					Count.UploadReaction(UploadReaction.PresignRefresh, 1L);
					if (uploadFailure2.UploadGone)
					{
						Count.UploadReaction(UploadReaction.UploadGone, 1L);
					}
					S3UploadFailure s = uploadFailure2.S3;
					if ((s == S3UploadFailure.DigestMismatch || s == S3UploadFailure.NoSuchUpload) ? true : false)
					{
						lock (uploadManifestLock)
						{
							ResetMultipartFileState(uploadManifestStore, uploadManifest, name);
						}
					}
					if ((await EnsureFreshUploadUrlAsync(name, ex4, fileCt).ConfigureAwait(continueOnCapturedContext: false)).AlreadyUploaded)
					{
						return SegmentFileUploadResult.AlreadyUploadedResult;
					}
					progress.ReportUploadedBytes(segment, name, 0L);
					progress.ReportFile(segment, name);
				}
			}
			return SegmentFileUploadResult.AlreadyUploadedResult;
		}
	}

	private void RunLocalRetentionSweep()
	{
		try
		{
			SweepExpiredLocalSegments();
		}
		catch (Exception ex)
		{
			Log("Retention sweep failed: " + ex.Message, "RunLocalRetentionSweep", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Upload\\UploadManager.cs", 1271);
		}
	}

	private static IReadOnlyList<IReadOnlyList<SegmentFile>> UploadPhases(IReadOnlyList<SegmentFile> files)
	{
		List<SegmentFile> list = (from f in files
			where !UploadFileKinds.IsVideoFile(f.Name)
			orderby UploadFileKinds.EvidenceRank(f.Name)
			select f).ThenBy((SegmentFile f) => f.Name, StringComparer.OrdinalIgnoreCase).ToList();
		List<SegmentFile> list2 = files.Where((SegmentFile f) => UploadFileKinds.IsVideoFile(f.Name)).OrderBy((SegmentFile f) => f.Name, StringComparer.OrdinalIgnoreCase).ToList();
		return new _003C_003Ez__ReadOnlyArray<IReadOnlyList<SegmentFile>>(new IReadOnlyList<SegmentFile>[2] { list, list2 });
	}

	private static bool ShouldRefreshUploadUrlsSoon(UploadRequestResponse uploadResp, long? issuedWallMs)
	{
		int valueOrDefault = uploadResp.ExpiresIn.GetValueOrDefault();
		if (valueOrDefault > 0 && issuedWallMs.HasValue)
		{
			long valueOrDefault2 = issuedWallMs.GetValueOrDefault();
			long num = valueOrDefault2 + (long)valueOrDefault * 1000L - (long)ProactivePresignedUrlRefreshWindow.TotalMilliseconds;
			return UploadManifestStore.NowMs() >= num;
		}
		return false;
	}

	internal static void ClearUploadedFileMarkers(UploadManifestStore store, UploadManifest manifest)
	{
		foreach (UploadFileManifest file in manifest.Files)
		{
			ResetFileUploadState(file, "verification_unavailable");
		}
		store.Save(manifest);
	}

	private static void ResetFileUploadState(UploadFileManifest file, string errorCode)
	{
		file.Uploaded = false;
		file.UploadedWallMs = null;
		file.UploadId = null;
		file.PartSizeBytes = null;
		file.Parts.Clear();
		file.LastErrorCode = errorCode;
	}

	internal static AlreadyUploadedOutcome AlreadyUploadedOutcomeFor(IReadOnlyDictionary<string, string>? serverFileHashes, IReadOnlyList<UploadFileDescriptor> localFiles)
	{
		if (serverFileHashes == null)
		{
			return AlreadyUploadedOutcome.TrustingDelete;
		}
		List<UploadFileDescriptor> list = localFiles.Where((UploadFileDescriptor file) => UploadFileKinds.IsSegmentPayloadFile(file.Name)).ToList();
		if (list.Count == 0)
		{
			return AlreadyUploadedOutcome.TrustingDelete;
		}
		if (!list.All((UploadFileDescriptor file) => serverFileHashes.TryGetValue(file.Name, out string value) && string.Equals(value, file.Sha256, StringComparison.OrdinalIgnoreCase)))
		{
			return AlreadyUploadedOutcome.Collision;
		}
		return AlreadyUploadedOutcome.Verified;
	}

	internal static bool UploadManifestHasAllFilesUploaded(UploadManifest manifest, IReadOnlyList<UploadFileDescriptor> files)
	{
		if (files.Count == 0)
		{
			return false;
		}
		foreach (UploadFileDescriptor file in files)
		{
			UploadFileManifest uploadFileManifest = manifest.Files.FirstOrDefault((UploadFileManifest f) => string.Equals(f.Name, file.Name, StringComparison.OrdinalIgnoreCase));
			if (uploadFileManifest == null || !uploadFileManifest.Uploaded || uploadFileManifest.SizeBytes != file.SizeBytes || !string.Equals(uploadFileManifest.Sha256, file.Sha256, StringComparison.OrdinalIgnoreCase))
			{
				return false;
			}
		}
		return true;
	}

	internal static bool HasCompleteMultipartParts(UploadFileManifest? fileManifest, long expectedSizeBytes)
	{
		if (fileManifest == null || fileManifest.Uploaded || !string.Equals(fileManifest.Mode, "multipart", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(fileManifest.UploadId) || fileManifest.SizeBytes != expectedSizeBytes || fileManifest.Parts.Count == 0)
		{
			return false;
		}
		return fileManifest.Parts.All((UploadPartManifest part) => part.PartNumber > 0 && part.SizeBytes > 0 && !string.IsNullOrWhiteSpace(part.ETag));
	}

	private static void ResetMultipartFileState(UploadManifestStore store, UploadManifest manifest, string fileName)
	{
		UploadFileManifest uploadFileManifest = manifest.Files.FirstOrDefault((UploadFileManifest f) => string.Equals(f.Name, fileName, StringComparison.OrdinalIgnoreCase));
		if (uploadFileManifest != null && string.Equals(uploadFileManifest.Mode, "multipart", StringComparison.OrdinalIgnoreCase))
		{
			ResetFileUploadState(uploadFileManifest, "multipart_state_reset");
			store.Save(manifest);
		}
	}

	private async Task UploadFileWithRetryAsync(SegmentInfo segment, string fileName, string filePath, long sizeBytes, string presignedUrl, string contentType, IReadOnlyDictionary<string, string>? headers, UploadBatchProgress progress, CancellationToken ct)
	{
		Exception ex = null;
		for (int attempt = 1; attempt <= 5; attempt++)
		{
			ct.ThrowIfCancellationRequested();
			if (attempt > 1)
			{
				TimeSpan delay = RetryDelay(attempt - 1, ex);
				Log($"  Retry {attempt}/5 for {Path.GetFileName(filePath)} in {delay.TotalSeconds:F1}s", "UploadFileWithRetryAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Upload\\UploadManager.cs", 1432);
				await Task.Delay(delay, ct).ConfigureAwait(continueOnCapturedContext: false);
			}
			try
			{
				await _fileUploadSlots.WaitAsync(ct).ConfigureAwait(continueOnCapturedContext: false);
				try
				{
					await S3Uploader.UploadFileAsync(filePath, presignedUrl, contentType, headers, (long uploaded, long _) =>
					{
						progress.ReportUploadedBytes(segment, fileName, uploaded);
					}, ct).ConfigureAwait(continueOnCapturedContext: false);
					return;
				}
				finally
				{
					_fileUploadSlots.Release();
				}
			}
			catch (S3UploadException ex2) when (ex2.Failure == S3UploadFailure.ObjectExists)
			{
				Log("  " + fileName + " already exists in S3 (412); treating as uploaded", "UploadFileWithRetryAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Upload\\UploadManager.cs", 1459);
				Count.UploadReaction(UploadReaction.ObjectExists, 1L);
				return;
			}
			catch (Exception ex3) when (attempt < 5 && RetriesInLoop(ex3, ct))
			{
				ex = ex3;
			}
			catch (Exception ex4)
			{
				ex = ex4;
				break;
			}
		}
		throw ex ?? new InvalidOperationException($"Upload failed for {Path.GetFileName(filePath)} ({sizeBytes} bytes).");
	}

	private async Task UploadMultipartFileAsync(SegmentInfo segment, SegmentFile file, string filePath, UploadUrlInfo urlInfo, UploadManifestStore store, UploadManifest manifest, object manifestLock, UploadBatchProgress progress, CancellationToken ct)
	{
		if (string.IsNullOrWhiteSpace(urlInfo.UploadId) || urlInfo.Parts == null || urlInfo.Parts.Count == 0)
		{
			throw new InvalidOperationException("Multipart descriptor is incomplete for " + file.Name + ".");
		}
		lock (manifestLock)
		{
			ConfigureMultipartFile(store, manifest, file, urlInfo);
		}
		List<UploadPartUrlInfo> orderedParts = urlInfo.Parts.OrderBy((UploadPartUrlInfo p) => p.PartNumber).ToList();
		long uploadStartedTicks = Stopwatch.GetTimestamp();
		long lastProgressLogTicks = uploadStartedTicks;
		object partProgressLock = new object();
		Dictionary<int, long> partProgress = new Dictionary<int, long>();
		int completedPartCount = 0;
		Log($"  Multipart upload start file={file.Name} size_bytes={file.SizeBytes} parts={orderedParts.Count} part_size_bytes={urlInfo.PartSizeBytes?.ToString() ?? "-"} part_concurrency={2}", "UploadMultipartFileAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Upload\\UploadManager.cs", 1507);
		await _largeVideoUploadSlot.WaitAsync(ct).ConfigureAwait(continueOnCapturedContext: false);
		try
		{
			List<UploadPartUrlInfo> list = new List<UploadPartUrlInfo>();
			lock (manifestLock)
			{
				UploadFileManifest uploadFileManifest = RequireUploadFileManifest(manifest, file.Name);
				foreach (UploadPartUrlInfo part in orderedParts)
				{
					UploadPartManifest uploadPartManifest = uploadFileManifest.Parts.FirstOrDefault((UploadPartManifest p) => p.PartNumber == part.PartNumber);
					if (uploadPartManifest == null)
					{
						throw new InvalidOperationException($"Missing multipart state for {file.Name} part {part.PartNumber}.");
					}
					if (!string.IsNullOrWhiteSpace(uploadPartManifest?.ETag))
					{
						partProgress[part.PartNumber] = uploadPartManifest.SizeBytes;
						int num = completedPartCount;
						completedPartCount = num + 1;
						if (ShouldLogMultipartPart(part.PartNumber, orderedParts.Count))
						{
							Log($"  Multipart part already uploaded file={file.Name} part={part.PartNumber}/{orderedParts.Count}", "UploadMultipartFileAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Upload\\UploadManager.cs", 1533);
						}
					}
					else
					{
						partProgress[part.PartNumber] = 0L;
						list.Add(part);
					}
				}
			}
			progress.ReportUploadedBytes(segment, file.Name, partProgress.Values.Sum());
			if (completedPartCount > 0)
			{
				Count.UploadReaction(UploadReaction.PartsResumed, completedPartCount);
			}
			CancellationTokenSource multipartCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
			SemaphoreSlim partUploadSlots;
			try
			{
				partUploadSlots = new SemaphoreSlim(2, 2);
				try
				{
					List<Task> partTasks = list.Select((UploadPartUrlInfo part2) => UploadMultipartPartWithRetryAsync(part2, multipartCts.Token)).ToList();
					try
					{
						while (partTasks.Count > 0)
						{
							Task task = await Task.WhenAny(partTasks).ConfigureAwait(continueOnCapturedContext: false);
							partTasks.Remove(task);
							await task.ConfigureAwait(continueOnCapturedContext: false);
						}
					}
					catch
					{
						multipartCts.Cancel();
						await DrainMultipartPartTasksAsync(partTasks).ConfigureAwait(continueOnCapturedContext: false);
						throw;
					}
					UploadFileManifest fileManifest;
					lock (manifestLock)
					{
						fileManifest = RequireUploadFileManifest(manifest, file.Name);
					}
					await CompleteMultipartFileAsync(segment, file, filePath, fileManifest, ct).ConfigureAwait(continueOnCapturedContext: false);
				}
				finally
				{
					if (partUploadSlots != null)
					{
						((IDisposable)partUploadSlots).Dispose();
					}
				}
			}
			finally
			{
				if (multipartCts != null)
				{
					((IDisposable)multipartCts).Dispose();
				}
			}
			async Task UploadMultipartPartWithRetryAsync(UploadPartUrlInfo uploadPartUrlInfo, CancellationToken partCt)
			{
				Exception ex = null;
				for (int attempt = 1; attempt <= 5; attempt++)
				{
					partCt.ThrowIfCancellationRequested();
					if (attempt > 1)
					{
						TimeSpan delay = RetryDelay(attempt - 1, ex);
						Log($"  Retry {attempt}/5 for {file.Name} part {uploadPartUrlInfo.PartNumber} in {delay.TotalSeconds:F1}s", "UploadMultipartFileAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Upload\\UploadManager.cs", 1577);
						await Task.Delay(delay, partCt).ConfigureAwait(continueOnCapturedContext: false);
					}
					try
					{
						bool fileSlotHeld = false;
						await partUploadSlots.WaitAsync(partCt).ConfigureAwait(continueOnCapturedContext: false);
						bool partSlotHeld = true;
						string text;
						try
						{
							await _fileUploadSlots.WaitAsync(partCt).ConfigureAwait(continueOnCapturedContext: false);
							fileSlotHeld = true;
							text = await S3Uploader.UploadFilePartAsync(filePath, uploadPartUrlInfo.Offset, uploadPartUrlInfo.SizeBytes, uploadPartUrlInfo.Url, uploadPartUrlInfo.Headers, (long uploaded, long _) =>
							{
								ReportMultipartPartProgress(uploadPartUrlInfo, uploaded, forceLog: false);
							}, partCt, uploadPartUrlInfo.PartNumber).ConfigureAwait(continueOnCapturedContext: false);
						}
						finally
						{
							if (fileSlotHeld)
							{
								_fileUploadSlots.Release();
							}
							if (partSlotHeld)
							{
								partUploadSlots.Release();
							}
						}
						if (string.IsNullOrWhiteSpace(text))
						{
							throw new InvalidOperationException($"S3 did not return an ETag for {file.Name} part {uploadPartUrlInfo.PartNumber}.");
						}
						lock (manifestLock)
						{
							MarkMultipartPart(store, manifest, file.Name, uploadPartUrlInfo.PartNumber, text);
						}
						ReportMultipartPartProgress(uploadPartUrlInfo, uploadPartUrlInfo.SizeBytes, forceLog: false);
						int value = Interlocked.Increment(ref completedPartCount);
						if (ShouldLogMultipartPart(uploadPartUrlInfo.PartNumber, orderedParts.Count))
						{
							LogMultipartProgress($"  Multipart part uploaded file={file.Name} part={uploadPartUrlInfo.PartNumber}/{orderedParts.Count} completed_parts={value}/{orderedParts.Count}", force: true);
						}
						return;
					}
					catch (Exception ex2) when (attempt < 5 && RetriesInLoop(ex2, partCt))
					{
						ex = ex2;
					}
				}
				throw ex ?? new InvalidOperationException($"Multipart upload failed for {file.Name} part {uploadPartUrlInfo.PartNumber}.");
			}
		}
		finally
		{
			_largeVideoUploadSlot.Release();
		}
		string? BuildMultipartProgressLogLocked(string prefix, bool force)
		{
			long timestamp = Stopwatch.GetTimestamp();
			if (force || !(TimeSpan.FromSeconds((double)(timestamp - lastProgressLogTicks) / (double)Stopwatch.Frequency) < MultipartProgressLogInterval))
			{
				lastProgressLogTicks = timestamp;
				double num2 = Math.Max((double)(timestamp - uploadStartedTicks) / (double)Stopwatch.Frequency, 0.001);
				long num3 = partProgress.Values.Sum();
				long num4 = Math.Max(file.SizeBytes - num3, 0L);
				double num5 = (double)num3 / num2;
				double value = ((num5 > 0.0) ? ((double)num4 / num5) : 0.0);
				double value2 = num5 * 8.0 / 1000000.0;
				double value3 = (double)num3 / 1024.0 / 1024.0;
				double value4 = (double)file.SizeBytes / 1024.0 / 1024.0;
				int value5 = Math.Max(orderedParts.Count - Volatile.Read(in completedPartCount), 0);
				return $"{prefix} uploaded_mib={value3:F1}/{value4:F1} elapsed_s={num2:F0} mbps={value2:F2} remaining_parts={value5} eta_s={value:F0}";
			}
			return null;
		}
		void LogMultipartProgress(string prefix, bool force)
		{
			string text;
			lock (partProgressLock)
			{
				text = BuildMultipartProgressLogLocked(prefix, force);
			}
			if (text != null)
			{
				Log(text, "UploadMultipartFileAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Upload\\UploadManager.cs", 1672);
			}
		}
		void ReportMultipartPartProgress(UploadPartUrlInfo uploadPartUrlInfo, long uploadedBytes, bool forceLog)
		{
			long uploadedBytes2;
			string text;
			lock (partProgressLock)
			{
				partProgress[uploadPartUrlInfo.PartNumber] = Math.Clamp(uploadedBytes, 0L, uploadPartUrlInfo.SizeBytes);
				uploadedBytes2 = partProgress.Values.Sum();
				text = BuildMultipartProgressLogLocked("  Multipart progress file=" + file.Name, forceLog);
			}
			progress.ReportUploadedBytes(segment, file.Name, uploadedBytes2);
			if (text != null)
			{
				Log(text, "UploadMultipartFileAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Upload\\UploadManager.cs", 1658);
			}
		}
	}

	private static async Task DrainMultipartPartTasksAsync(IReadOnlyList<Task> partTasks)
	{
		if (partTasks.Count == 0)
		{
			return;
		}
		try
		{
			await Task.WhenAll(partTasks).ConfigureAwait(continueOnCapturedContext: false);
		}
		catch
		{
		}
	}

	private async Task<UploadCompleteResponse> CompleteUploadWithRetryAsync(string clientSessionId, int segmentIndex, long totalUploadedBytes, IReadOnlyList<string> fileNames, long claimedDurationMs, CancellationToken ct)
	{
		Exception ex = null;
		for (int attempt = 1; attempt <= 3; attempt++)
		{
			ct.ThrowIfCancellationRequested();
			if (attempt > 1)
			{
				TimeSpan delay = RetryDelay(attempt - 1, ex);
				Log($"  Retry completeUpload {attempt}/3 for segment {segmentIndex} in {delay.TotalSeconds:F1}s", "CompleteUploadWithRetryAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Upload\\UploadManager.cs", 1754);
				await Task.Delay(delay, ct).ConfigureAwait(continueOnCapturedContext: false);
			}
			try
			{
				return await _api.CompleteUploadAsync(clientSessionId, segmentIndex, totalUploadedBytes, fileNames, _auth.DeviceId, claimedDurationMs, ct).ConfigureAwait(continueOnCapturedContext: false);
			}
			catch (Exception ex2) when (attempt < 3 && RetriesInLoop(ex2, ct))
			{
				ex = ex2;
			}
			catch (Exception ex3)
			{
				ex = ex3;
				break;
			}
		}
		throw ex ?? new InvalidOperationException($"upload-complete failed for {clientSessionId} seg={segmentIndex}.");
	}

	private async Task<MultipartCompleteResponse> CompleteMultipartUploadWithRetryAsync(MultipartCompleteRequest request, CancellationToken ct)
	{
		Exception ex = null;
		for (int attempt = 1; attempt <= 3; attempt++)
		{
			ct.ThrowIfCancellationRequested();
			if (attempt > 1)
			{
				TimeSpan delay = RetryDelay(attempt - 1, ex);
				Log($"  Retry multipart complete {attempt}/3 for {request.FileName} seg={request.SegmentIndex} in {delay.TotalSeconds:F1}s", "CompleteMultipartUploadWithRetryAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Upload\\UploadManager.cs", 1796);
				await Task.Delay(delay, ct).ConfigureAwait(continueOnCapturedContext: false);
			}
			try
			{
				APIClient api = _api;
				api.SetAccessToken(await _auth.GetValidTokenAsync().ConfigureAwait(continueOnCapturedContext: false));
				return await _api.CompleteMultipartUploadAsync(request, ct).ConfigureAwait(continueOnCapturedContext: false);
			}
			catch (Exception ex2) when (attempt < 3 && RetriesInLoop(ex2, ct))
			{
				ex = ex2;
			}
			catch (Exception ex3)
			{
				ex = ex3;
				break;
			}
		}
		throw ex ?? new InvalidOperationException($"upload-multipart-complete failed for {request.ClientSessionId} seg={request.SegmentIndex} file={request.FileName}.");
	}

	private async Task CompleteMultipartFileAsync(SegmentInfo segment, SegmentFile file, string filePath, UploadFileManifest fileManifest, CancellationToken ct)
	{
		IReadOnlyList<string> partDigests = UploadFileDigests.For(filePath).PartSha256;
		if (partDigests?.Count != fileManifest.Parts.Count || fileManifest.PartSizeBytes != 67108864)
		{
			partDigests = null;
		}
		List<MultipartPartComplete> list = (from p in fileManifest.Parts
			orderby p.PartNumber
			select new MultipartPartComplete
			{
				PartNumber = p.PartNumber,
				ETag = (p.ETag ?? ""),
				Sha256 = partDigests?[p.PartNumber - 1]
			}).ToList();
		if (list.Any((MultipartPartComplete p) => string.IsNullOrWhiteSpace(p.ETag)))
		{
			throw new InvalidOperationException("Multipart upload is missing ETags for " + file.Name + ".");
		}
		Log($"  Completing multipart upload file={file.Name} parts={list.Count} upload_id={fileManifest.UploadId}", "CompleteMultipartFileAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Upload\\UploadManager.cs", 1854);
		MultipartCompleteResponse multipartCompleteResponse;
		string healCode = default;
		try
		{
			multipartCompleteResponse = await CompleteMultipartUploadWithRetryAsync(new MultipartCompleteRequest
			{
				ClientSessionId = segment.ClientSessionId,
				DeviceId = (_auth.DeviceId ?? ""),
				SegmentIndex = segment.SegmentIndex,
				FileName = file.Name,
				UploadId = (fileManifest.UploadId ?? ""),
				ExpectedSizeBytes = file.SizeBytes,
				Parts = list
			}, ct).ConfigureAwait(continueOnCapturedContext: false);
		}
		catch (APIClientException ex) when (((Func<bool>)delegate
		{
			// Could not convert BlockContainer to single expression
			healCode = UploadFailurePolicy.Classify(ex).HealCode;
			return healCode != null;
		}).Invoke())
		{
			throw new MultipartCompleteRejectedException(file.Name, healCode);
		}
		if (!multipartCompleteResponse.Status.Equals("completed", StringComparison.OrdinalIgnoreCase) || multipartCompleteResponse.SizeBytes != file.SizeBytes)
		{
			throw new InvalidOperationException("Multipart complete failed for " + file.Name + ".");
		}
		Log($"  Multipart complete status={multipartCompleteResponse.Status} file={file.Name} size_bytes={multipartCompleteResponse.SizeBytes}", "CompleteMultipartFileAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Upload\\UploadManager.cs", 1882);
	}

	private async Task CompleteRecoverableMultipartUploadsAsync(SegmentInfo segment, IReadOnlyList<SegmentFile> files, UploadManifestStore store, UploadManifest manifest, UploadBatchProgress progress, CancellationToken ct)
	{
		foreach (SegmentFile file in files.Where((SegmentFile f) => UploadFileKinds.IsVideoFile(f.Name)))
		{
			UploadFileManifest fileManifest = manifest.Files.FirstOrDefault((UploadFileManifest f) => string.Equals(f.Name, file.Name, StringComparison.OrdinalIgnoreCase));
			if (HasCompleteMultipartParts(fileManifest, file.SizeBytes))
			{
				Log($"  Recovering completed multipart state file={file.Name} parts={fileManifest.Parts.Count}", "CompleteRecoverableMultipartUploadsAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Upload\\UploadManager.cs", 1902);
				try
				{
					await CompleteMultipartFileAsync(segment, file, Path.Combine(segment.SessionDir, file.Name), fileManifest, ct).ConfigureAwait(continueOnCapturedContext: false);
					MarkUploadFile(store, manifest, file.Name, fileManifest.Sha256, uploaded: true);
					progress.ReportUploadedBytes(segment, file.Name, file.SizeBytes);
					progress.CompleteFile(segment, file.Name);
				}
				catch (APIClientException ex) when (ex.IsSessionExpired || ex.IsUpdateRequired || ex.IsDeviceOrProfileBlocked)
				{
					throw;
				}
				catch (MultipartCompleteRejectedException)
				{
					throw;
				}
				catch (OperationCanceledException) when (ct.IsCancellationRequested)
				{
					throw;
				}
				catch (Exception ex4) when (UploadFailurePolicy.RecoveryResetsMultipartState(ex4))
				{
					Log("  Could not recover multipart state for " + file.Name + "; resetting local multipart markers: " + ex4.Message, "CompleteRecoverableMultipartUploadsAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Upload\\UploadManager.cs", 1931);
					ResetMultipartFileState(store, manifest, file.Name);
				}
				catch (Exception ex5)
				{
					Log("  Could not recover multipart state for " + file.Name + "; keeping its parts for the next completion: " + ex5.Message, "CompleteRecoverableMultipartUploadsAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Upload\\UploadManager.cs", 1939);
				}
			}
		}
	}

	private void HandleUploadCompleteResponse(UploadCompleteResponse completeResp, SegmentInfo segment, UploadManifestStore uploadManifestStore, UploadManifest uploadManifest)
	{
		Log($"  upload-complete status={completeResp.Status} seg={segment.SegmentIndex} segment_id={completeResp.RecordingSegmentId} duration_ms={completeResp.UploadedDurationMs} credit_microusd={completeResp.CreditedAmountMicrousd}", "HandleUploadCompleteResponse", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Upload\\UploadManager.cs", 1950);
		switch (completeResp.Status.ToLowerInvariant())
		{
		case "completed":
		case "uploaded_but_not_credited":
			SetUploadState(uploadManifestStore, uploadManifest, UploadManifestState.Complete);
			CleanupUploadedOrDefer(segment);
			return;
		case "session_expired":
			throw new APIClientException("desktop-v2-upload-complete", 401, completeResp.Status);
		case "update_required":
			throw APIClient.UpdateRequiredByStatus("desktop-v2-upload-complete", completeResp.Status);
		}
		throw new InvalidOperationException($"Unhandled upload-complete status '{completeResp.Status}' for {segment.ClientSessionId} seg={segment.SegmentIndex}.");
	}

	private static bool RetriesInLoop(Exception ex, CancellationToken ct)
	{
		if (ex is OperationCanceledException && ct.IsCancellationRequested)
		{
			return false;
		}
		UploadFailure uploadFailure = UploadFailurePolicy.Classify(ex);
		if (!uploadFailure.EscapesLoop)
		{
			return uploadFailure.RetryInLoop;
		}
		return false;
	}

	private static TimeSpan RetryDelay(int attempt, Exception? lastError = null)
	{
		if (lastError is APIClientException { RetryAfter: { } retryAfter } && retryAfter > TimeSpan.Zero)
		{
			if (!(retryAfter > TimeSpan.FromMinutes(10.0)))
			{
				return retryAfter;
			}
			return TimeSpan.FromMinutes(10.0);
		}
		return TimeSpan.FromSeconds(Math.Min(Math.Pow(2.0, attempt), 20.0) * Random.Shared.NextDouble() * 0.5 + Math.Min(Math.Pow(2.0, attempt), 20.0) * 0.5);
	}

	private static bool ShouldLogMultipartPart(int partNumber, int totalParts)
	{
		if (totalParts > 20 && partNumber != 1 && partNumber != totalParts)
		{
			return partNumber % 10 == 0;
		}
		return true;
	}

	private async Task<UploadRequestResponse> RefreshUploadUrlsAsync(SegmentInfo segment, IReadOnlyList<UploadFileDescriptor> fileDescs, long durationMs, long idleMs, DateTimeOffset capturedAt, CancellationToken ct)
	{
		Log($"  Refreshing presigned URLs for {segment.ClientSessionId} seg={segment.SegmentIndex}", "RefreshUploadUrlsAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Upload\\UploadManager.cs", 2016);
		APIClient api = _api;
		api.SetAccessToken(await _auth.GetValidTokenAsync().ConfigureAwait(continueOnCapturedContext: false));
		UploadRequestResponse uploadRequestResponse = await _api.RequestUploadAsync(segment.ClientSessionId, segment.SegmentIndex, durationMs, idleMs, capturedAt, fileDescs, _auth.DeviceId, ct).ConfigureAwait(continueOnCapturedContext: false);
		switch (uploadRequestResponse.Status.ToLowerInvariant())
		{
		case "already_uploaded":
			Log("  URL refresh: segment already uploaded remaining_files=skipped", "RefreshUploadUrlsAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Upload\\UploadManager.cs", 2031);
			return uploadRequestResponse;
		case "pending":
			Log($"  Presigned URLs refreshed ({uploadRequestResponse.UploadUrls.Count} URLs)", "RefreshUploadUrlsAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Upload\\UploadManager.cs", 2035);
			return uploadRequestResponse;
		case "session_expired":
			throw new APIClientException("desktop-v2-upload-request", 401, uploadRequestResponse.Status);
		case "update_required":
			throw APIClient.UpdateRequiredByStatus("desktop-v2-upload-request", uploadRequestResponse.Status);
		default:
			throw new InvalidOperationException($"Unhandled upload-request status '{uploadRequestResponse.Status}' on refresh for {segment.ClientSessionId} seg={segment.SegmentIndex}.");
		}
	}

	internal static bool CleanupUploaded(SegmentInfo segment)
	{
		List<(string, string)> item = SegmentFate.DeleteFiles(segment.SessionDir, segment.SegmentIndex, null, keepManifestOnFailure: false).Failures;
		if (item.Count > 0)
		{
			KuroLog.LocalOnly("upload", $"segment cleanup incomplete session={segment.ClientSessionId} seg={segment.SegmentIndex} failed={item.Count} file={item[0].Item1}: {item[0].Item2}", "CleanupUploaded", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Upload\\UploadManager.cs", 2065);
		}
		SegmentFate.RemoveSessionDirIfEmpty(segment.SessionDir);
		string manifestName = SegmentManifestStore.ManifestFileName(segment.SegmentIndex);
		return !item.Any(((string Name, string Error) failure) => string.Equals(failure.Name, manifestName, StringComparison.OrdinalIgnoreCase));
	}

	internal bool CleanupUploadedOrDefer(SegmentInfo segment)
	{
		if (CleanupUploaded(segment))
		{
			return true;
		}
		_pendingCleanups[SegmentKey(segment.ClientSessionId, segment.SegmentIndex)] = segment;
		return false;
	}

	internal void RetryPendingCleanups()
	{
		foreach (KeyValuePair<string, SegmentInfo> pendingCleanup in _pendingCleanups)
		{
			pendingCleanup.Deconstruct(out var key, out var value);
			string key2 = key;
			if (CleanupUploaded(value))
			{
				_pendingCleanups.TryRemove(key2, out value);
			}
		}
	}

	internal bool IsHeld(string key)
	{
		if (!_inFlightSegments.ContainsKey(key))
		{
			return _pendingCleanups.ContainsKey(key);
		}
		return true;
	}

	private static void SyncUploadManifestFiles(UploadManifest manifest, IReadOnlyList<UploadFileDescriptor> files)
	{
		Dictionary<string, UploadFileManifest> previous = manifest.Files.ToDictionary((UploadFileManifest f) => f.Name, StringComparer.OrdinalIgnoreCase);
		manifest.Files = files.Select((UploadFileDescriptor file) => (previous.TryGetValue(file.Name, out var value) && value.SizeBytes == file.SizeBytes && string.Equals(value.Sha256, file.Sha256, StringComparison.OrdinalIgnoreCase)) ? value : new UploadFileManifest
		{
			Name = file.Name,
			SizeBytes = file.SizeBytes,
			Sha256 = file.Sha256
		}).ToList();
	}

	private static void SetUploadState(UploadManifestStore store, UploadManifest manifest, UploadManifestState state)
	{
		manifest.State = state;
		store.Save(manifest);
	}

	private static void MarkUploadFile(UploadManifestStore store, UploadManifest manifest, string fileName, string? sha256, bool uploaded)
	{
		UploadFileManifest uploadFileManifest = manifest.Files.FirstOrDefault((UploadFileManifest f) => string.Equals(f.Name, fileName, StringComparison.OrdinalIgnoreCase));
		if (uploadFileManifest == null)
		{
			uploadFileManifest = new UploadFileManifest
			{
				Name = fileName
			};
			manifest.Files.Add(uploadFileManifest);
		}
		uploadFileManifest.Uploaded = uploaded;
		if (!string.IsNullOrWhiteSpace(sha256))
		{
			uploadFileManifest.Sha256 = sha256;
		}
		uploadFileManifest.UploadedWallMs = (uploaded ? new long?(UploadManifestStore.NowMs()) : ((long?)null));
		uploadFileManifest.LastErrorCode = null;
		store.Save(manifest);
	}

	internal static void ConfigureMultipartFile(UploadManifestStore store, UploadManifest manifest, SegmentFile file, UploadUrlInfo urlInfo)
	{
		UploadFileManifest uploadFileManifest = manifest.Files.FirstOrDefault((UploadFileManifest f) => string.Equals(f.Name, file.Name, StringComparison.OrdinalIgnoreCase));
		if (uploadFileManifest == null)
		{
			uploadFileManifest = new UploadFileManifest
			{
				Name = file.Name
			};
			manifest.Files.Add(uploadFileManifest);
		}
		bool num = !string.Equals(uploadFileManifest.UploadId, urlInfo.UploadId, StringComparison.Ordinal) || uploadFileManifest.SizeBytes != file.SizeBytes || uploadFileManifest.Parts.Count != (urlInfo.Parts?.Count ?? 0);
		uploadFileManifest.SizeBytes = file.SizeBytes;
		uploadFileManifest.Mode = "multipart";
		uploadFileManifest.Key = urlInfo.Key;
		uploadFileManifest.UploadId = urlInfo.UploadId;
		uploadFileManifest.PartSizeBytes = urlInfo.PartSizeBytes;
		uploadFileManifest.Sha256 = urlInfo.Sha256;
		if (num)
		{
			uploadFileManifest.Uploaded = false;
			uploadFileManifest.UploadedWallMs = null;
			uploadFileManifest.Parts = (from p in urlInfo.Parts ?? new List<UploadPartUrlInfo>()
				orderby p.PartNumber
				select new UploadPartManifest
				{
					PartNumber = p.PartNumber,
					Offset = p.Offset,
					SizeBytes = p.SizeBytes
				}).ToList();
		}
		store.Save(manifest);
	}

	private static UploadFileManifest RequireUploadFileManifest(UploadManifest manifest, string fileName)
	{
		return manifest.Files.FirstOrDefault((UploadFileManifest f) => string.Equals(f.Name, fileName, StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidOperationException("Upload manifest is missing " + fileName + ".");
	}

	private static void MarkMultipartPart(UploadManifestStore store, UploadManifest manifest, string fileName, int partNumber, string etag)
	{
		UploadFileManifest uploadFileManifest = RequireUploadFileManifest(manifest, fileName);
		UploadPartManifest? uploadPartManifest = uploadFileManifest.Parts.FirstOrDefault((UploadPartManifest p) => p.PartNumber == partNumber) ?? throw new InvalidOperationException($"Upload manifest is missing {fileName} part {partNumber}.");
		uploadPartManifest.ETag = etag;
		uploadPartManifest.UploadedWallMs = UploadManifestStore.NowMs();
		uploadFileManifest.LastErrorCode = null;
		store.Save(manifest);
	}

	private void SweepExpiredLocalSegments()
	{
		DateTimeOffset utcNow = DateTimeOffset.UtcNow;
		if (!(utcNow - _lastRetentionSweepAt < RetentionSweepInterval))
		{
			_lastRetentionSweepAt = utcNow;
			TimeSpan backlogPurgeAge = UploadRetentionPolicy.BacklogPurgeAge;
			long seconds = _opportunity.Seconds;
			string item = _getActiveSegment().Item1;
			SweepSessionDirectories(_storage.EnumerateSessionDirectories(), item, utcNow, backlogPurgeAge, seconds);
		}
	}

	internal SweepTally SweepSessionDirectories(IEnumerable<string> sessionDirs, string? activeDir, DateTimeOffset now, TimeSpan backlogPurgeAge, long opportunitySeconds)
	{
		SweepTally sweepTally = new SweepTally();
		int num = 0;
		foreach (string sessionDir in sessionDirs)
		{
			if (activeDir != null && string.Equals(sessionDir, activeDir, StringComparison.OrdinalIgnoreCase))
			{
				num++;
				continue;
			}
			try
			{
				SweepSessionDirectory(sessionDir, now, backlogPurgeAge, opportunitySeconds, sweepTally);
			}
			catch (Exception ex)
			{
				KuroLog.LocalOnly("upload", "Retention sweep skipped " + Path.GetFileName(sessionDir) + ": " + ex.Message, "SweepSessionDirectories", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Upload\\UploadManager.cs", 2334);
			}
		}
		if (sweepTally.Deleted + sweepTally.Failed == 0)
		{
			return sweepTally;
		}
		string value = string.Join(",", from pair in sweepTally.BacklogStates
			orderby pair.Key.ToString()
			select $"{pair.Key.ToString().ToLowerInvariant()}={pair.Value}");
		Log($"Backlog purge deleted_segments={sweepTally.BacklogPurged} failed_segments={sweepTally.Failed} sessions={sweepTally.BacklogSessions.Count} duration_hours={(double)sweepTally.BacklogDurationMs / 3600000.0:F2} bytes_mb={(double)sweepTally.BacklogBytes / 1048576.0:F1} oldest_opportunity_hours={sweepTally.OldestOpportunity.TotalHours:F1} cutoff_hours={backlogPurgeAge.TotalHours:F0} states={value} quarantine_expired={sweepTally.QuarantineExpired} corrupt={sweepTally.CorruptExpired} orphan={sweepTally.OrphanExpired} debris={sweepTally.DebrisExpired} skipped_active_sessions={num} skipped_recovery_sessions={sweepTally.SkippedRecoverySessions} skipped_inflight_segments={sweepTally.SkippedInFlightSegments}", "SweepSessionDirectories", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Upload\\UploadManager.cs", 2348);
		return sweepTally;
	}

	internal void SweepSessionDirectory(string sessionDir, DateTimeOffset now, TimeSpan backlogPurgeAge, long opportunitySeconds, SweepTally tally)
	{
		string fileName = Path.GetFileName(sessionDir);
		List<(int, string)> list = new List<(int, string)>();
		HashSet<int> hashSet = new HashSet<int>();
		Dictionary<int, DateTime> dictionary = new Dictionary<int, DateTime>();
		foreach (FileInfo item3 in new DirectoryInfo(sessionDir).EnumerateFiles())
		{
			if (string.Equals(item3.Name, ".active-session.json", StringComparison.OrdinalIgnoreCase))
			{
				tally.SkippedRecoverySessions++;
				return;
			}
			if (SegmentManifestStore.TryParseSegmentIndex(item3.Name, out var segmentIndex, out var kind))
			{
				DateTime value;
				if (kind == SegmentArtifactKind.Manifest)
				{
					list.Add((segmentIndex, item3.FullName));
					hashSet.Add(segmentIndex);
				}
				else if (!dictionary.TryGetValue(segmentIndex, out value) || item3.LastWriteTimeUtc > value)
				{
					dictionary[segmentIndex] = item3.LastWriteTimeUtc;
				}
			}
		}
		int num = list.Count;
		foreach (var item4 in list)
		{
			int item = item4.Item1;
			string item2 = item4.Item2;
			SegmentManifestLoadResult segmentManifestLoadResult = SegmentManifestStore.TryLoadDetailed(item2);
			Exception readError = segmentManifestLoadResult.ReadError;
			if (readError != null)
			{
				KuroLog.LocalOnly("upload", "segment manifest read failed file=" + Path.GetFileName(item2) + " error=" + KuroLog.ExceptionSummary(readError), "SweepSessionDirectory", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Upload\\UploadManager.cs", 2406);
				continue;
			}
			SegmentManifest manifest = segmentManifestLoadResult.Manifest;
			if (manifest != null && SegmentManifestStore.IsUploadCandidateState(manifest.State))
			{
				TimeSpan timeSpan = UploadOpportunityAge(sessionDir, item, opportunitySeconds);
				if (timeSpan < backlogPurgeAge)
				{
					continue;
				}
				if (IsHeld(SegmentKey(fileName, item)))
				{
					tally.SkippedInFlightSegments++;
				}
				else
				{
					if (tally.Deleted >= 200)
					{
						continue;
					}
					UploadManifest uploadState = UploadManifestStore.TryLoad(UploadManifestStore.ManifestPath(sessionDir, item));
					long? num2 = SegmentFate.Delete(sessionDir, item, DeleteCause.BacklogPurged, manifest, uploadState, timeSpan.TotalHours);
					if (num2.HasValue)
					{
						long valueOrDefault = num2.GetValueOrDefault();
						num--;
						tally.BacklogPurged++;
						tally.BacklogBytes += valueOrDefault;
						tally.BacklogDurationMs += SegmentDurationMs(manifest);
						tally.BacklogSessions.Add(string.IsNullOrWhiteSpace(manifest.SessionId) ? fileName : manifest.SessionId);
						tally.BacklogStates[manifest.State] = tally.BacklogStates.GetValueOrDefault(manifest.State) + 1;
						if (timeSpan > tally.OldestOpportunity)
						{
							tally.OldestOpportunity = timeSpan;
						}
					}
					else
					{
						tally.Failed++;
					}
				}
				continue;
			}
			DeleteCause? deleteCause;
			if (!segmentManifestLoadResult.Corrupt)
			{
				SegmentManifest? manifest2 = segmentManifestLoadResult.Manifest;
				deleteCause = ((manifest2 != null && manifest2.State == SegmentManifestState.Quarantined) ? new DeleteCause?(DeleteCause.QuarantineExpired) : ((DeleteCause?)null));
			}
			else
			{
				deleteCause = DeleteCause.CorruptManifestExpired;
			}
			DeleteCause? deleteCause2 = deleteCause;
			if (!deleteCause2.HasValue || now - segmentManifestLoadResult.LastWriteUtc < SegmentFate.QuarantinedRetention || tally.Deleted >= 200)
			{
				continue;
			}
			UploadManifest uploadState2 = UploadManifestStore.TryLoad(UploadManifestStore.ManifestPath(sessionDir, item));
			if (!SegmentFate.Delete(sessionDir, item, deleteCause2.Value, segmentManifestLoadResult.Manifest, uploadState2).HasValue)
			{
				tally.Failed++;
				continue;
			}
			num--;
			if (segmentManifestLoadResult.Corrupt)
			{
				tally.CorruptExpired++;
			}
			else
			{
				tally.QuarantineExpired++;
			}
		}
		foreach (var (num4, dateTime2) in dictionary)
		{
			if (!hashSet.Contains(num4) && !(now - dateTime2 < backlogPurgeAge) && !IsHeld(SegmentKey(fileName, num4)) && tally.Deleted < 200)
			{
				if (SegmentFate.Delete(sessionDir, num4, DeleteCause.OrphanExpired).HasValue)
				{
					tally.OrphanExpired++;
				}
				else
				{
					tally.Failed++;
				}
			}
		}
		if (num > 0 || SegmentFate.RemoveSessionDirIfEmpty(sessionDir) || tally.Deleted >= 200)
		{
			return;
		}
		DateTime? dateTime3 = null;
		long num5 = 0L;
		foreach (FileInfo item5 in new DirectoryInfo(sessionDir).EnumerateFiles("*", SearchOption.AllDirectories))
		{
			num5 += item5.Length;
			if (dateTime3.HasValue)
			{
				DateTime valueOrDefault2 = dateTime3.GetValueOrDefault();
				if (!(item5.LastWriteTimeUtc > valueOrDefault2))
				{
					continue;
				}
			}
			dateTime3 = item5.LastWriteTimeUtc;
		}
		if (!(now - (dateTime3 ?? Directory.GetLastWriteTimeUtc(sessionDir)) < backlogPurgeAge))
		{
			if (SegmentFate.DeleteDirectory(sessionDir, num5))
			{
				tally.DebrisExpired++;
			}
			else
			{
				tally.Failed++;
			}
		}
	}

	internal static TimeSpan UploadOpportunityAge(string sessionDir, int segmentIndex, long opportunitySeconds)
	{
		long? num = UploadMarkerFiles.ReadOpportunityStamp(sessionDir, segmentIndex);
		if (num.HasValue)
		{
			long valueOrDefault = num.GetValueOrDefault();
			if (valueOrDefault <= opportunitySeconds)
			{
				return TimeSpan.FromSeconds(opportunitySeconds - valueOrDefault);
			}
		}
		UploadMarkerFiles.WriteOpportunityStamp(sessionDir, segmentIndex, opportunitySeconds);
		return TimeSpan.Zero;
	}

	private static long SegmentDurationMs(SegmentManifest manifest)
	{
		long? claimedDurationMs = manifest.ClaimedDurationMs;
		if (claimedDurationMs.HasValue && claimedDurationMs.GetValueOrDefault() > 0)
		{
			return manifest.ClaimedDurationMs.Value;
		}
		if (manifest.StartedWallMs > 0)
		{
			claimedDurationMs = manifest.EndedWallMs;
			if (claimedDurationMs.HasValue)
			{
				long valueOrDefault = claimedDurationMs.GetValueOrDefault();
				if (valueOrDefault >= manifest.StartedWallMs)
				{
					return valueOrDefault - manifest.StartedWallMs;
				}
			}
		}
		return 0L;
	}

	private long MarkUploadFailure(SegmentInfo segment, UploadManifestStore store, UploadManifest manifest, UploadFailure failure, string errorMessage)
	{
		long num;
		if (!failure.ServerRejected)
		{
			num = ((failure.ErrorKind != ErrorKind.Cancelled) ? TransientRetryDelaySeconds(SegmentKey(segment.ClientSessionId, segment.SegmentIndex)) : Math.Max(30, AppConfig.BackgroundUploadRetryDelaysSeconds[0]));
		}
		else
		{
			int num2 = UploadMarkerFiles.IncrementAttemptCount(segment.SessionDir, segment.SegmentIndex);
			num = EscalatedRetryDelaySeconds(num2);
			if (num > AppConfig.BackgroundUploadRetryDelaysSeconds.Max())
			{
				Log($"  Segment {segment.ClientSessionId} seg={segment.SegmentIndex} server-rejected {num2} time(s); durable retry escalated to {(double)num / 3600.0:F1}h", "MarkUploadFailure", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Upload\\UploadManager.cs", 2604);
			}
		}
		manifest.State = UploadManifestState.Ready;
		manifest.NextRetryWallMs = UploadManifestStore.NowMs() + num * 1000;
		manifest.LastErrorCode = failure.DurableErrorCode;
		manifest.LastErrorMessage = errorMessage;
		store.Save(manifest);
		return num;
	}

	private long TransientRetryDelaySeconds(string key)
	{
		int num = _transientFailureCounts.AddOrUpdate(key, 1, (string _, int current) => current + 1);
		int[] backgroundUploadRetryDelaysSeconds = AppConfig.BackgroundUploadRetryDelaysSeconds;
		return Math.Max(30, backgroundUploadRetryDelaysSeconds[Math.Clamp(num - 1, 0, backgroundUploadRetryDelaysSeconds.Length - 1)]);
	}

	private static long EscalatedRetryDelaySeconds(int attempts)
	{
		int[] backgroundUploadRetryDelaysSeconds = AppConfig.BackgroundUploadRetryDelaysSeconds;
		if (attempts <= 10)
		{
			int num = Math.Clamp(attempts - 1, 0, backgroundUploadRetryDelaysSeconds.Length - 1);
			return Math.Max(30, backgroundUploadRetryDelaysSeconds[num]);
		}
		int num2;
		if (attempts <= 13)
		{
			num2 = 3600;
		}
		else
		{
			num2 = ((attempts > 16) ? 86400 : 21600);
		}
		return num2;
	}

	internal SegmentFateRecordedException QuarantineHealEscalated(SegmentInfo segment, UploadManifestStore store, UploadManifest manifest, string healCode, int heals, string durableErrorCode, Exception cause)
	{
		string text = $"{healCode} x{heals}";
		SegmentManifest segmentManifest = SegmentFate.Quarantine(segment.SessionDir, segment.SegmentIndex, null, QuarantineReason.HealEscalated, FateOrigin.Upload, text, manifest.Attempts);
		string text2 = "integrity heal cap reached (" + text + ")";
		manifest.State = UploadManifestState.Ready;
		manifest.NextRetryWallMs = ((segmentManifest == null) ? new long?(UploadManifestStore.NowMs() + (long)SegmentFate.QuarantinedRetention.TotalMilliseconds) : ((long?)null));
		manifest.LastErrorCode = durableErrorCode;
		manifest.LastErrorMessage = text2;
		try
		{
			store.Save(manifest);
		}
		catch (Exception ex)
		{
			KuroLog.LocalOnly("upload", $"heal cap state not written session={segment.ClientSessionId} seg={segment.SegmentIndex} error={KuroLog.ExceptionSummary(ex)}", "QuarantineHealEscalated", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Upload\\UploadManager.cs", 2696);
		}
		_transientFailureCounts.TryRemove(SegmentKey(segment.ClientSessionId, segment.SegmentIndex), out var _);
		return new SegmentFateRecordedException(text2, durableErrorCode, cause, countsAsFailedUpload: true);
	}

	internal static SegmentFateRecordedException RecordAlreadyUploadedCollision(SegmentInfo segment, UploadManifestStore store, UploadManifest uploadState, string context)
	{
		Count.AlreadyUploaded(AlreadyUploadedOutcome.Collision, 1L);
		if (SegmentFate.Delete(segment.SessionDir, segment.SegmentIndex, DeleteCause.AlreadyUploadedCollision, null, uploadState, null, context).HasValue)
		{
			SegmentFate.RemoveSessionDirIfEmpty(segment.SessionDir);
		}
		else
		{
			uploadState.NextRetryWallMs = UploadManifestStore.NowMs() + (long)SegmentFate.QuarantinedRetention.TotalMilliseconds;
			try
			{
				store.Save(uploadState);
			}
			catch (Exception ex)
			{
				KuroLog.LocalOnly("upload", $"collision hold not written session={segment.ClientSessionId} seg={segment.SegmentIndex} error={KuroLog.ExceptionSummary(ex)}", "RecordAlreadyUploadedCollision", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Upload\\UploadManager.cs", 2740);
			}
		}
		return new SegmentFateRecordedException("already_uploaded remote hash mismatch (segment index collision)", "already_uploaded_hash_mismatch", null, countsAsFailedUpload: false);
	}

	internal void CloseFatedSegment(string key, SegmentFateRecordedException fate)
	{
		if (fate.CountsAsFailedUpload)
		{
			Count.SegmentUploadFail(TelemetryMap.UploadFailReasonFor(UploadFailurePolicy.Classify(fate).ErrorKind), 1L);
		}
		_retryScheduler.Forget(key);
		_segmentDiscovery.ForgetSegment(key);
		_transientFailureCounts.TryRemove(key, out var _);
	}

	internal static UploadFileDescriptor DescribeUploadFile(string sessionDir, SegmentFile file)
	{
		UploadFileDigest uploadFileDigest = UploadFileDigests.For(Path.Combine(sessionDir, file.Name));
		return new UploadFileDescriptor
		{
			Name = file.Name,
			ContentType = UploadFileKinds.ContentTypeFor(file.Name),
			SizeBytes = file.SizeBytes,
			Sha256 = uploadFileDigest.Sha256,
			PartSha256 = uploadFileDigest.PartSha256
		};
	}

	private static string SegmentKey(string clientSessionId, int segmentIndex)
	{
		return $"{clientSessionId}:{segmentIndex}";
	}

	private void ReportProgress(UploadProgressInfo info)
	{
		try
		{
			ProgressChanged?.Invoke(info);
		}
		catch (Exception value)
		{
			Log($"ProgressChanged callback failed: {value}", "ReportProgress", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Upload\\UploadManager.cs", 2792);
		}
	}

	private bool ConsumePendingUploadRequest()
	{
		lock (_pendingLock)
		{
			if (!_pendingRecordingUploadRequested)
			{
				return false;
			}
			_pendingRecordingUploadRequested = false;
			return true;
		}
	}
}
