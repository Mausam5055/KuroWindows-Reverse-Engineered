using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ClientTelemetry;
using KuroWindows.Auth;
using KuroWindows.Diagnostics;

namespace KuroWindows.Sync;

public sealed class DesktopSyncManager : IDisposable
{
	private readonly AuthManager _auth;

	private readonly APIClient _api;

	private readonly DesktopConfigCache _cache;

	private readonly SemaphoreSlim _syncLock = new SemaphoreSlim(1, 1);

	private readonly object _stateLock = new object();

	private readonly object _lifecycleLock = new object();

	private DesktopSyncSnapshot _snapshot = new DesktopSyncSnapshot();

	private CachedDesktopConfig? _acceptedCache;

	private CancellationTokenSource? _runCts;

	private CancellationTokenSource? _delayCts;

	private int _immediateSyncRequested;

	private Task? _runTask;

	private int _consecutiveFailures;

	private bool _loggedAuthUnavailableSkip;

	private string? _lastSyncFailureDescription;

	private DateTimeOffset _lastImmediateSyncLoggedAt = DateTimeOffset.MinValue;

	private static readonly TimeSpan ImmediateSyncLogInterval = TimeSpan.FromMinutes(5.0);

	public DesktopSyncSnapshot Snapshot
	{
		get
		{
			lock (_stateLock)
			{
				return _snapshot;
			}
		}
	}

	public event EventHandler<DesktopSyncSnapshot>? SnapshotChanged;

	public event Action<JsonElement>? CommunicationsHintReceived;

	public DesktopSyncManager(AuthManager auth, APIClient? api = null, DesktopConfigCache? cache = null)
	{
		_auth = auth;
		_api = api ?? new APIClient();
		_cache = cache ?? new DesktopConfigCache();
	}

	public void Start()
	{
		lock (_lifecycleLock)
		{
			if (_runCts != null)
			{
				return;
			}
			_consecutiveFailures = 0;
			_loggedAuthUnavailableSkip = false;
			lock (_stateLock)
			{
				_acceptedCache = _cache.Load();
			}
			CancellationTokenSource runCts = new CancellationTokenSource();
			_runCts = runCts;
			_runTask = Task.Run(() => RunAsync(runCts.Token));
		}
		KuroLog.Sync("DesktopSyncManager: control-plane sync started", "Start", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Sync\\DesktopSyncManager.cs", 78);
	}

	public void RequestImmediateSync()
	{
		bool flag = false;
		lock (_lifecycleLock)
		{
			Interlocked.Exchange(ref _immediateSyncRequested, 1);
			try
			{
				_delayCts?.Cancel();
			}
			catch
			{
			}
			DateTimeOffset utcNow = DateTimeOffset.UtcNow;
			if (utcNow - _lastImmediateSyncLoggedAt >= ImmediateSyncLogInterval)
			{
				_lastImmediateSyncLoggedAt = utcNow;
				flag = true;
			}
		}
		if (flag)
		{
			KuroLog.Sync("DesktopSyncManager: immediate sync requested", "RequestImmediateSync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Sync\\DesktopSyncManager.cs", 102);
		}
	}

	public void Stop(bool clearCachedConfig)
	{
		CancellationTokenSource runCts;
		CancellationTokenSource delayCts;
		Task runTask;
		lock (_lifecycleLock)
		{
			runCts = _runCts;
			delayCts = _delayCts;
			runTask = _runTask;
			if (runCts == null && delayCts == null && runTask == null)
			{
				if (clearCachedConfig)
				{
					_cache.Delete();
				}
				return;
			}
			_runCts = null;
			_delayCts = null;
			_runTask = null;
		}
		try
		{
			delayCts?.Cancel();
		}
		catch
		{
		}
		try
		{
			runCts?.Cancel();
		}
		catch
		{
		}
		if (runCts != null)
		{
			if (runTask == null)
			{
				runCts.Dispose();
			}
			else
			{
				runTask.ContinueWith((Task _) =>
				{
					runCts.Dispose();
				}, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
			}
		}
		if (clearCachedConfig)
		{
			_cache.Delete();
		}
		_consecutiveFailures = 0;
		_loggedAuthUnavailableSkip = false;
		_lastSyncFailureDescription = null;
		Interlocked.Exchange(ref _immediateSyncRequested, 0);
		lock (_stateLock)
		{
			_acceptedCache = null;
		}
		SetSnapshot(new DesktopSyncSnapshot());
		KuroLog.Sync("DesktopSyncManager: control-plane sync stopped", "Stop", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Sync\\DesktopSyncManager.cs", 161);
	}

	private async Task RunAsync(CancellationToken cancellationToken)
	{
		while (!cancellationToken.IsCancellationRequested)
		{
			Interlocked.Exchange(ref _immediateSyncRequested, 0);
			await SyncCurrentStateAsync(cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
			if (cancellationToken.IsCancellationRequested)
			{
				break;
			}
			if (Interlocked.Exchange(ref _immediateSyncRequested, 0) == 1)
			{
				continue;
			}
			using CancellationTokenSource delayCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			lock (_lifecycleLock)
			{
				if (!cancellationToken.IsCancellationRequested)
				{
					_delayCts = delayCts;
				}
			}
			try
			{
				await Task.Delay(TimeSpan.FromSeconds(NextDelaySeconds()), delayCts.Token).ConfigureAwait(continueOnCapturedContext: false);
			}
			catch (OperationCanceledException)
			{
				if (cancellationToken.IsCancellationRequested)
				{
					break;
				}
			}
			finally
			{
				lock (_lifecycleLock)
				{
					if (_delayCts == delayCts)
					{
						_delayCts = null;
					}
				}
			}
		}
	}

	private async Task SyncCurrentStateAsync(CancellationToken cancellationToken)
	{
		if (!_auth.IsSignedIn || string.IsNullOrWhiteSpace(_auth.DeviceId))
		{
			if (!_loggedAuthUnavailableSkip)
			{
				_loggedAuthUnavailableSkip = true;
				KuroLog.Sync("DesktopSyncManager: sync skipped because auth/device is unavailable", "SyncCurrentStateAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Sync\\DesktopSyncManager.cs", 226);
			}
			SetSnapshot(new DesktopSyncSnapshot
			{
				Status = "error",
				LastError = "signed-in desktop sync requires a registered device"
			});
			return;
		}
		await _syncLock.WaitAsync(cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
		try
		{
			CachedDesktopConfig cached;
			lock (_stateLock)
			{
				cached = _acceptedCache;
			}
			string accessToken = await _auth.GetValidTokenAsync().ConfigureAwait(continueOnCapturedContext: false);
			DesktopSyncRequest request = new DesktopSyncRequest
			{
				DeviceId = _auth.DeviceId,
				KnownConfigVersion = (cached?.ConfigVersion ?? 0)
			};
			_api.SetAccessToken(accessToken);
			DesktopSyncResponse desktopSyncResponse = await _api.DesktopSyncAsync(request, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
			if (!cancellationToken.IsCancellationRequested)
			{
				int consecutiveFailures = _consecutiveFailures;
				_consecutiveFailures = 0;
				_loggedAuthUnavailableSkip = false;
				_lastSyncFailureDescription = null;
				if (consecutiveFailures > 0)
				{
					KuroLog.Sync($"DesktopSyncManager: sync recovered failures={consecutiveFailures}", "SyncCurrentStateAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Sync\\DesktopSyncManager.cs", 265);
				}
				try
				{
					CommunicationsHintReceived?.Invoke(desktopSyncResponse.Communications);
				}
				catch
				{
				}
				ApplyResponse(desktopSyncResponse, cached);
			}
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
		}
		catch (Exception ex2)
		{
			string text = ExceptionDescriber.Describe(ex2);
			string status = Snapshot.Status;
			bool flag = ((status == "error" || status == "access_denied") ? true : false);
			if (!flag || !string.Equals(text, _lastSyncFailureDescription, StringComparison.Ordinal))
			{
				_lastSyncFailureDescription = text;
				KuroLog.Sync("DesktopSyncManager: sync failed: " + text, "SyncCurrentStateAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Sync\\DesktopSyncManager.cs", 285);
			}
			_consecutiveFailures++;
			if (IsAuthoritativeDenial(ex2))
			{
				SetSnapshot(new DesktopSyncSnapshot
				{
					SyncedAt = DateTimeOffset.UtcNow,
					Status = "access_denied",
					LastError = text
				});
			}
			else
			{
				DesktopSyncSnapshot snapshot = Snapshot;
				SetSnapshot(snapshot with
				{
					SyncedAt = DateTimeOffset.UtcNow,
					Status = "error",
					LastError = text
				});
			}
		}
		finally
		{
			_syncLock.Release();
		}
	}

	private int NextDelaySeconds()
	{
		DesktopSyncSnapshot snapshot = Snapshot;
		string status = snapshot.Status;
		if ((status == "error" || status == "access_denied") ? true : false)
		{
			return Math.Min(300, 30 * (1 << Math.Min(3, Math.Max(0, _consecutiveFailures - 1))));
		}
		return Math.Max(30, snapshot.NextSyncAfterSeconds);
	}

	private DesktopSyncSnapshot ApplyResponse(DesktopSyncResponse response, CachedDesktopConfig? cached)
	{
		DesktopSyncSnapshot snapshot = Snapshot;
		DesktopConfig desktopConfig;
		if (response.ConfigChanged)
		{
			if (response.Config == null)
			{
				throw new InvalidOperationException("desktop_sync returned config_changed=true without config.");
			}
			desktopConfig = response.Config.Normalize();
			if (!desktopConfig.TryValidate(out string error))
			{
				throw new InvalidOperationException("desktop_sync returned invalid config: " + error);
			}
			CachedDesktopConfig cachedDesktopConfig = new CachedDesktopConfig
			{
				SchemaVersion = 7,
				ConfigVersion = response.ConfigVersion,
				CachedAt = DateTimeOffset.UtcNow,
				Config = desktopConfig
			};
			if (!cachedDesktopConfig.TryValidate(out error))
			{
				throw new InvalidOperationException("desktop_sync returned config incompatible with this client: " + error);
			}
			lock (_stateLock)
			{
				_acceptedCache = cachedDesktopConfig;
			}
			KuroLog.Sync($"DesktopSyncManager: config changed version={response.ConfigVersion} logging={desktopConfig.LoggingEnabled} recorderCore={desktopConfig.RecorderCore ?? "-"}", "ApplyResponse", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Sync\\DesktopSyncManager.cs", 362);
			TelemetryEvent e = Ev.ConfigApplied(response.ConfigVersion, TelemetryMap.TelemetryLevelFor(desktopConfig.Telemetry?.Level), desktopConfig.RecorderCore);
			bool flag = Telemetry.Records(e);
			Telemetry.Emit(e);
			KuroTelemetry.ApplyConfig(desktopConfig);
			if (!flag)
			{
				Telemetry.Emit(e);
			}
			try
			{
				_cache.Save(response.ConfigVersion, desktopConfig);
			}
			catch (Exception ex)
			{
				KuroLog.Sync("DesktopSyncManager: config cache save failed (policy adopted in-memory): " + ex.GetType().Name + ": " + ex.Message, "ApplyResponse", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Sync\\DesktopSyncManager.cs", 384);
			}
		}
		else
		{
			if (cached == null)
			{
				throw new InvalidOperationException("desktop_sync returned unchanged config but no cached config is available.");
			}
			desktopConfig = cached.Config.Normalize();
			if (ShouldLogCachedConfigAccepted(snapshot, response, desktopConfig))
			{
				KuroLog.Sync($"DesktopSyncManager: config accepted from cache version={response.ConfigVersion} logging={desktopConfig.LoggingEnabled} recorderCore={desktopConfig.RecorderCore ?? "-"}", "ApplyResponse", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Sync\\DesktopSyncManager.cs", 396);
			}
		}
		return SetSnapshot(new DesktopSyncSnapshot
		{
			HasConfig = true,
			ConfigVersion = response.ConfigVersion,
			Config = desktopConfig,
			LastSeenAt = response.LastSeenAt,
			SyncedAt = DateTimeOffset.UtcNow,
			NextSyncAfterSeconds = Math.Max(30, response.NextSyncAfterSeconds),
			Status = "synced"
		});
	}

	private static bool ShouldLogCachedConfigAccepted(DesktopSyncSnapshot previous, DesktopSyncResponse response, DesktopConfig config)
	{
		if (!(previous.Status != "synced") && previous.HasConfig && previous.ConfigVersion == response.ConfigVersion && previous.Config.LoggingEnabled == config.LoggingEnabled)
		{
			return !string.Equals(previous.Config.RecorderCore, config.RecorderCore, StringComparison.Ordinal);
		}
		return true;
	}

	private static bool IsAuthoritativeDenial(Exception exception)
	{
		if (!(exception is APIClientException ex))
		{
			return false;
		}
		if (ex.IsSessionExpired || ex.IsDeviceOrProfileBlocked)
		{
			return true;
		}
		string value = $"{ex.Code} {ex.ServerMessage} {ex.ResponseBody}";
		return new string[6] { "unauthorized", "device_not_found", "device_access_denied", "device_banned", "profile_not_found", "profile_not_active" }.Any((string token) => value.Contains(token, StringComparison.OrdinalIgnoreCase));
	}

	private DesktopSyncSnapshot SetSnapshot(DesktopSyncSnapshot snapshot)
	{
		lock (_stateLock)
		{
			_snapshot = snapshot;
		}
		EventHandler<DesktopSyncSnapshot> eventHandler = SnapshotChanged;
		if (eventHandler == null)
		{
			return snapshot;
		}
		Delegate[] invocationList = eventHandler.GetInvocationList();
		for (int i = 0; i < invocationList.Length; i++)
		{
			EventHandler<DesktopSyncSnapshot> eventHandler2 = (EventHandler<DesktopSyncSnapshot>)invocationList[i];
			try
			{
				eventHandler2(this, snapshot);
			}
			catch (Exception value)
			{
				KuroLog.Sync($"DesktopSyncManager: SnapshotChanged subscriber failed: {value}", "SetSnapshot", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Sync\\DesktopSyncManager.cs", 470);
			}
		}
		return snapshot;
	}

	public void Dispose()
	{
		Stop(clearCachedConfig: false);
	}
}
