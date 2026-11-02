using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using ClientTelemetry;
using Velopack;

namespace DesktopUpdates;

public sealed class UpdateService : IDisposable
{
	private enum Step
	{
		Done,
		Retry,
		Again,
		Offline
	}

	internal static readonly TimeSpan HeartbeatInterval = TimeSpan.FromMinutes(15.0);

	private static readonly TimeSpan[] Ladder = new TimeSpan[6]
	{
		TimeSpan.FromSeconds(30.0),
		TimeSpan.FromMinutes(1.0),
		TimeSpan.FromMinutes(2.0),
		TimeSpan.FromMinutes(5.0),
		TimeSpan.FromMinutes(10.0),
		TimeSpan.FromMinutes(15.0)
	};

	private static readonly TimeSpan[] HandoffLadder = new TimeSpan[4]
	{
		TimeSpan.FromMinutes(15.0),
		TimeSpan.FromHours(1.0),
		TimeSpan.FromHours(6.0),
		TimeSpan.FromHours(24.0)
	};

	private static readonly TimeSpan NudgeSpacing = TimeSpan.FromSeconds(30.0);

	private static readonly TimeSpan UploadDrainBudget = TimeSpan.FromSeconds(20.0);

	private static readonly TimeSpan SignInSettlePoll = TimeSpan.FromSeconds(5.0);

	private static readonly TimeSpan QuitLockWait = TimeSpan.FromSeconds(3.0);

	private readonly IUpdateHost _host;

	private readonly UpdateLog _log;

	private readonly UpdateRecord _record;

	private readonly string _appVersion;

	private readonly bool _feedEnabled;

	private readonly Func<Task<(UpdateCheckStatus Status, string? ErrorMessage)>> _checkVersion;

	private readonly UpdateFeed? _feed;

	private readonly string _feedDisabledReason;

	private readonly SemaphoreSlim _op = new SemaphoreSlim(1, 1);

	private readonly SemaphoreSlim _wake = new SemaphoreSlim(0, 1);

	private readonly CancellationTokenSource _life = new CancellationTokenSource();

	private readonly object _sync = new object();

	private readonly object _handoffGate = new object();

	private bool _passRunning;

	private Task _pass = Task.CompletedTask;

	private long _requests;

	private UpdateTrigger _trigger;

	private bool _userUrgent;

	private CancellationTokenSource? _momentWait;

	private bool _started;

	private bool _shutdown;

	private Timer? _heartbeat;

	private int _beating;

	private long _lastBeatTimestamp;

	private string? _lastFloorCheck;

	private volatile string? _target;

	private volatile string _userCheck = "idle";

	private readonly Dictionary<string, int> _attempts = new Dictionary<string, int>(StringComparer.Ordinal);

	private readonly HashSet<string> _stagedReported = new HashSet<string>(StringComparer.Ordinal);

	private readonly HashSet<string> _attemptCounted = new HashSet<string>(StringComparer.Ordinal);

	private readonly Dictionary<string, (long StartedTimestamp, long BytesAtStart)> _downloads = new Dictionary<string, (long, long)>(StringComparer.Ordinal);

	private bool _disabledLogged;

	private bool _nothingToInstallReported;

	private string? _lastOutcome;

	public VersionFloor Floor { get; }

	public UpdateSnapshot Snapshot
	{
		get
		{
			bool isBelowFloor = Floor.IsBelowFloor;
			string target = _target;
			string userCheck = _userCheck;
			string status;
			if (isBelowFloor)
			{
				status = "required";
			}
			else if (userCheck == "checking")
			{
				status = "checking";
			}
			else
			{
				status = ((target != null) ? "available" : userCheck);
			}
			return new UpdateSnapshot(isBelowFloor, status, target);
		}
	}

	public event Action? Changed;

	public UpdateService(UpdateOptions options, IUpdateHost host)
	{
		_host = host;
		_log = UpdateLog.From(options);
		_appVersion = options.AppVersion;
		_feedEnabled = options.FeedEnabled;
		_checkVersion = options.CheckVersionAsync;
		_record = new UpdateRecord(options.LocalDataFolder, options.AppVersion, _log);
		Floor = new VersionFloor(_log, options.AppVersion);
		_feed = UpdateFeed.Create(options, _log, out _feedDisabledReason);
		Floor.Attach(OnFloorHeard, OnFloorCleared);
	}

	public static void ReportPreviousRun(UpdateOptions options, string? priorVersion, string? priorExitReason)
	{
		new UpdateRecord(options.LocalDataFolder, options.AppVersion, UpdateLog.From(options)).ReportLaunch(priorVersion, priorExitReason);
	}

	internal static TimeSpan LadderStep(int failures)
	{
		return Ladder[Math.Clamp(failures - 1, 0, Ladder.Length - 1)];
	}

	internal static TimeSpan HandoffRetryStep(int handoffs, bool urgent)
	{
		if (!urgent)
		{
			return HandoffLadder[Math.Clamp(handoffs - 1, 0, HandoffLadder.Length - 1)];
		}
		return LadderStep(handoffs);
	}

	internal static bool ReportsAttempt(int attempt)
	{
		if (attempt > 0)
		{
			return (attempt & (attempt - 1)) == 0;
		}
		return false;
	}

	public Task EnsureLatestAsync(UpdateTrigger trigger, bool urgent)
	{
		lock (_sync)
		{
			if (_shutdown)
			{
				return Task.CompletedTask;
			}
			bool flag = false;
			if (!_passRunning || Rank(trigger) > Rank(_trigger))
			{
				flag = _passRunning;
				_trigger = (_passRunning ? Stronger(_trigger, trigger) : trigger);
			}
			if (urgent && trigger != UpdateTrigger.Required && !_userUrgent)
			{
				_userUrgent = true;
				flag = true;
			}
			if (trigger == UpdateTrigger.User)
			{
				flag = true;
				if (!urgent && _target == null)
				{
					_userCheck = "checking";
				}
			}
			_requests++;
			if (_passRunning)
			{
				if (flag)
				{
					SignalWake();
					if (urgent || Floor.IsBelowFloor)
					{
						_momentWait?.Cancel();
					}
				}
			}
			else
			{
				_passRunning = true;
				_pass = Task.Run((Func<Task?>)RunPassAsync);
			}
		}
		RaiseChanged();
		return _pass;
	}

	public void Start()
	{
		lock (_sync)
		{
			if (_started || _shutdown)
			{
				return;
			}
			_started = true;
		}
		if (_feed == null)
		{
			_log.Update("update routine: feed disabled reason=" + _feedDisabledReason + "; the floor check runs, nothing installs", "Start", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\shared\\dotnet\\DesktopUpdates\\UpdateService.cs", 248);
		}
		if (!_feedEnabled)
		{
			BeatAsync(UpdateTrigger.Heartbeat, "launch");
			return;
		}
		_heartbeat = new Timer((object? _) =>
		{
			_ = BeatAsync(UpdateTrigger.Heartbeat, "timer");
		});
		NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
		BeatAsync(UpdateTrigger.Heartbeat, "launch");
	}

	public void CheckNow()
	{
		if (Snapshot.AvailableVersion == null)
		{
			_userCheck = "checking";
			RaiseChanged();
		}
		BeatAsync(UpdateTrigger.User, "user");
	}

	public void InstallNow()
	{
		EnsureLatestAsync(UpdateTrigger.User, urgent: true);
	}

	public void NotifyWake()
	{
		Nudge("wake");
	}

	public void Shutdown()
	{
		lock (_handoffGate)
		{
			lock (_sync)
			{
				if (_shutdown)
				{
					return;
				}
				_shutdown = true;
			}
		}
		try
		{
			_heartbeat?.Dispose();
		}
		catch
		{
		}
		_heartbeat = null;
		NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
		try
		{
			_life.Cancel();
		}
		catch (ObjectDisposedException)
		{
		}
	}

	public void Dispose()
	{
		Shutdown();
	}

	private async Task<bool> DownloadAsync(UpdateInfo info, string version, UpdateTrigger trigger, CancellationToken ct)
	{
		bool flag;
		(long StartedTimestamp, long BytesAtStart) started;
		lock (_sync)
		{
			flag = !_downloads.TryGetValue(version, out started);
			if (flag)
			{
				started = (StartedTimestamp: Stopwatch.GetTimestamp(), BytesAtStart: _feed.BytesReceived);
				_downloads[version] = started;
			}
		}
		if (flag)
		{
			_log.Update($"update download begin version={version} trigger={trigger.ToWire()} bytes={info.TargetFullRelease.Size}", "DownloadAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\shared\\dotnet\\DesktopUpdates\\UpdateService.Download.cs", 26);
		}
		try
		{
			await _feed.DownloadAsync(info, ct).ConfigureAwait(continueOnCapturedContext: false);
		}
		catch (Exception ex) when (!ct.IsCancellationRequested)
		{
			ReportFailure(UpdateStage.Download, trigger, version, ex);
			return false;
		}
		bool flag2;
		lock (_sync)
		{
			flag2 = _stagedReported.Add(version);
		}
		if (flag2)
		{
			long value = _feed.BytesReceived - started.BytesAtStart;
			long value2 = (long)Stopwatch.GetElapsedTime(started.StartedTimestamp).TotalMilliseconds;
			bool lastDownloadWasDelta = _feed.LastDownloadWasDelta;
			Telemetry.Emit(Ev.UpdateStaged(version, value, lastDownloadWasDelta, value2, trigger));
			_log.Update($"update staged version={version} bytes={value} delta={lastDownloadWasDelta} duration_ms={value2} trigger={trigger.ToWire()}", "DownloadAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\shared\\dotnet\\DesktopUpdates\\UpdateService.Download.cs", 51);
		}
		_record.NoteStaged(version, trigger, landedNow: true);
		return true;
	}

	private void OnFloorHeard(bool began)
	{
		if (began)
		{
			lock (_sync)
			{
				_momentWait?.Cancel();
			}
			RaiseChanged();
			EnterBelowFloorAsync();
		}
		EnsureLatestAsync(UpdateTrigger.Required, urgent: true);
	}

	private async Task EnterBelowFloorAsync()
	{
		try
		{
			await _host.EnterBelowFloorAsync().ConfigureAwait(continueOnCapturedContext: false);
		}
		catch (Exception ex)
		{
			_log.Update("update required: ending recording failed: " + _log.ExceptionSummary(ex), "EnterBelowFloorAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\shared\\dotnet\\DesktopUpdates\\UpdateService.Floor.cs", 33);
		}
	}

	private void OnFloorCleared()
	{
		lock (_sync)
		{
			_nothingToInstallReported = false;
		}
		_host.ExitBelowFloor();
		RaiseChanged();
	}

	private void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e)
	{
		if (e.IsAvailable)
		{
			Nudge("network");
		}
	}

	private void Nudge(string why)
	{
		SignalWake();
		if (!(Stopwatch.GetElapsedTime(Interlocked.Read(in _lastBeatTimestamp)) < NudgeSpacing))
		{
			BeatAsync(UpdateTrigger.Heartbeat, why);
		}
	}

	private async Task BeatAsync(UpdateTrigger trigger, string why)
	{
		if (_shutdown)
		{
			return;
		}
		if (Interlocked.Exchange(ref _beating, 1) != 0)
		{
			if (trigger == UpdateTrigger.User)
			{
				EnsureLatestAsync(UpdateTrigger.User, urgent: false);
			}
			return;
		}
		Interlocked.Exchange(ref _lastBeatTimestamp, Stopwatch.GetTimestamp());
		TimeSpan next = Jittered(HeartbeatInterval);
		try
		{
			var (updateCheckStatus, text) = await _checkVersion().ConfigureAwait(continueOnCapturedContext: false);
			string text2;
			switch (updateCheckStatus)
			{
			case UpdateCheckStatus.UpToDate:
				if (Floor.CheckPassed() == FloorCheck.Deferred)
				{
					next = VersionFloor.ClearQuietPeriod;
				}
				text2 = "ok";
				break;
			case UpdateCheckStatus.UpdateRequired:
				text2 = "required";
				break;
			default:
				text2 = "error";
				break;
			}
			if (text2 != _lastFloorCheck)
			{
				_lastFloorCheck = text2;
				_log.Update((text2 == "error") ? ("version check status=error why=" + why + ": " + (text ?? "unknown")) : ("version check status=" + text2 + " why=" + why), "BeatAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\shared\\dotnet\\DesktopUpdates\\UpdateService.Heartbeat.cs", 76);
			}
			if (!_shutdown)
			{
				EnsureLatestAsync(trigger, urgent: false);
			}
		}
		catch (Exception ex)
		{
			_log.Update("version check failed why=" + why + ": " + _log.ExceptionSummary(ex), "BeatAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\shared\\dotnet\\DesktopUpdates\\UpdateService.Heartbeat.cs", 88);
		}
		finally
		{
			Volatile.Write(ref _beating, 0);
			try
			{
				_heartbeat?.Change(next, Timeout.InfiniteTimeSpan);
			}
			catch (ObjectDisposedException)
			{
			}
		}
	}

	private static TimeSpan Jittered(TimeSpan interval)
	{
		return TimeSpan.FromMilliseconds(interval.TotalMilliseconds * (0.9 + Random.Shared.NextDouble() * 0.2));
	}

	public bool InstallStagedAtLaunch(bool launchedInBackground)
	{
		if (_feed == null)
		{
			return false;
		}
		VelopackAsset staged;
		try
		{
			staged = _feed.Staged;
		}
		catch (Exception ex)
		{
			_log.Update("update install at launch: staged package unreadable: " + _log.ExceptionSummary(ex), "InstallStagedAtLaunch", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\shared\\dotnet\\DesktopUpdates\\UpdateService.Install.cs", 28);
			return false;
		}
		if (staged == null)
		{
			return false;
		}
		string text = staged.Version.ToString();
		TimeSpan timeSpan = _record.ApplyBackoffRemaining(text, Floor.IsBelowFloor);
		if (timeSpan > TimeSpan.Zero)
		{
			_log.Update($"update install at launch deferred version={text} retry_in_s={(int)Math.Ceiling(timeSpan.TotalSeconds)}", "InstallStagedAtLaunch", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\shared\\dotnet\\DesktopUpdates\\UpdateService.Install.cs", 41);
			return false;
		}
		if (!_op.Wait(0))
		{
			return false;
		}
		try
		{
			_record.NoteStaged(text, UpdateTrigger.Launch, landedNow: false);
			return HandOff(staged, UpdateTrigger.Launch, !launchedInBackground);
		}
		catch (Exception ex2)
		{
			_host.MarkExitAborted("update_apply_failed", ex2);
			ReportFailure(UpdateStage.Apply, UpdateTrigger.Launch, text, ex2);
			return false;
		}
		finally
		{
			_op.Release();
		}
	}

	public void InstallStagedOnQuit()
	{
		Shutdown();
		if (_feed == null)
		{
			return;
		}
		if (!_op.Wait(QuitLockWait))
		{
			_log.Update("update install on quit skipped: another update step is still in flight", "InstallStagedOnQuit", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\shared\\dotnet\\DesktopUpdates\\UpdateService.Install.cs", 82);
			return;
		}
		string text = null;
		try
		{
			VelopackAsset staged = _feed.Staged;
			if (!(staged == null))
			{
				text = staged.Version.ToString();
				_record.NoteStaged(text, UpdateTrigger.Quit, landedNow: false);
				_record.NoteHandoff(text, UpdateTrigger.Quit);
				CountAttemptOnce(text);
				_log.Update("update install on quit version=" + text + " trigger=quit relaunch=none", "InstallStagedOnQuit", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\shared\\dotnet\\DesktopUpdates\\UpdateService.Install.cs", 99);
				_feed.InstallOnExit(staged);
			}
		}
		catch (Exception ex)
		{
			ReportFailure(UpdateStage.Apply, UpdateTrigger.Quit, text, ex);
		}
		finally
		{
			_op.Release();
		}
	}

	private async Task<Step> InstallAsync(UpdateInfo info, string version, UpdateTrigger trigger, bool urgent)
	{
		CancellationToken ct = _life.Token;
		TimeSpan timeSpan = _record.ApplyBackoffRemaining(version, urgent);
		if (timeSpan > TimeSpan.Zero)
		{
			LogOutcome("backoff:" + version, $"update install deferred version={version} retry_in_s={(int)Math.Ceiling(timeSpan.TotalSeconds)}");
			await WaitAsync(timeSpan).ConfigureAwait(continueOnCapturedContext: false);
			return Step.Again;
		}
		while (!urgent && _host.SignInInFlight)
		{
			await Task.Delay(SignInSettlePoll, ct).ConfigureAwait(continueOnCapturedContext: false);
			lock (_sync)
			{
				urgent = _userUrgent || Floor.IsBelowFloor;
			}
		}
		if (_host.IsShuttingDown)
		{
			return Step.Done;
		}
		CancellationTokenSource moment = CancellationTokenSource.CreateLinkedTokenSource(ct);
		lock (_sync)
		{
			_momentWait = moment;
			urgent = _userUrgent || Floor.IsBelowFloor;
		}
		bool flag = false;
		bool flag2;
		try
		{
			LogOutcome($"moment:{version}:{urgent}", urgent ? $"update install version={version} trigger={trigger.ToWire()}: ending recording now" : $"update install version={version} trigger={trigger.ToWire()}: waits for the next segment boundary, idle or lock");
			flag2 = await _host.EndRecordingForInstallAsync(urgent, moment.Token).ConfigureAwait(continueOnCapturedContext: false);
			flag = !flag2 && moment.IsCancellationRequested;
		}
		catch (Exception ex) when (!ct.IsCancellationRequested)
		{
			_log.Update("update install version=" + version + ": ending recording failed: " + _log.ExceptionSummary(ex), "InstallAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\shared\\dotnet\\DesktopUpdates\\UpdateService.Install.cs", 162);
			_host.ResumeRecordingAfterAbortedInstall();
			return Step.Retry;
		}
		finally
		{
			lock (_sync)
			{
				if (_momentWait == moment)
				{
					_momentWait = null;
				}
			}
			moment.Dispose();
		}
		if (!flag2)
		{
			ct.ThrowIfCancellationRequested();
			return (!flag) ? Step.Retry : Step.Again;
		}
		try
		{
			await _host.DrainUploadsAsync(UploadDrainBudget).ConfigureAwait(continueOnCapturedContext: false);
			await _op.WaitAsync(ct).ConfigureAwait(continueOnCapturedContext: false);
			try
			{
				bool userUrgent;
				lock (_sync)
				{
					trigger = _trigger;
					userUrgent = _userUrgent;
				}
				bool foreground = userUrgent || _host.WindowInFront;
				try
				{
					if (!HandOff(info.TargetFullRelease, trigger, foreground))
					{
						_host.ResumeRecordingAfterAbortedInstall();
						return Step.Done;
					}
				}
				finally
				{
					lock (_sync)
					{
						_userUrgent = false;
					}
				}
			}
			finally
			{
				_op.Release();
			}
		}
		catch (OperationCanceledException) when (ct.IsCancellationRequested)
		{
			_host.ResumeRecordingAfterAbortedInstall();
			throw;
		}
		catch (Exception ex3)
		{
			_host.MarkExitAborted("update_apply_failed", ex3);
			ReportFailure(UpdateStage.Apply, trigger, version, ex3);
			_host.ResumeRecordingAfterAbortedInstall();
			return Step.Retry;
		}
		return Step.Done;
	}

	private bool HandOff(VelopackAsset asset, UpdateTrigger trigger, bool foreground)
	{
		string text = asset.Version.ToString();
		lock (_handoffGate)
		{
			if (_shutdown || _host.IsShuttingDown)
			{
				_log.Update("update install version=" + text + " abandoned: the app is shutting down", "HandOff", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\shared\\dotnet\\DesktopUpdates\\UpdateService.Install.cs", 257);
				return false;
			}
			_record.NoteHandoff(text, trigger);
			CountAttemptOnce(text);
			_log.Update($"update install version={text} from={_appVersion} trigger={trigger.ToWire()} relaunch={(foreground ? "foreground" : "background")}", "HandOff", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\shared\\dotnet\\DesktopUpdates\\UpdateService.Install.cs", 263);
			_host.MarkCleanExitForInstall("update_restart");
			Telemetry.Drain(TimeSpan.Zero);
			_feed.InstallAndRelaunch(asset, foreground ? null : new string[1] { "--background" });
		}
		try
		{
			_host.StopHelperProcessesForInstall("update_restart");
		}
		catch
		{
		}
		try
		{
			_host.CloseWindowForInstall();
		}
		catch
		{
		}
		Environment.Exit(0);
		return true;
	}

	private async Task RunPassAsync()
	{
		int failures = 0;
		try
		{
			while (!_life.IsCancellationRequested)
			{
				while (_wake.Wait(0))
				{
				}
				long serial;
				UpdateTrigger trigger;
				bool urgent;
				lock (_sync)
				{
					serial = _requests;
					trigger = _trigger;
					urgent = _userUrgent || Floor.IsBelowFloor;
				}
				switch (await RunOnceAsync(trigger, urgent).ConfigureAwait(continueOnCapturedContext: false))
				{
				case Step.Again:
					failures = 0;
					continue;
				case Step.Retry:
					failures++;
					await WaitAsync(LadderStep(failures)).ConfigureAwait(continueOnCapturedContext: false);
					continue;
				case Step.Offline:
					await WaitAsync(Ladder[^1]).ConfigureAwait(continueOnCapturedContext: false);
					continue;
				}
				lock (_sync)
				{
					if (_requests == serial)
					{
						EndPassLocked();
						return;
					}
				}
				failures = 0;
			}
		}
		catch (OperationCanceledException) when (_life.IsCancellationRequested)
		{
		}
		catch (Exception ex2)
		{
			_log.Update("update routine failed: " + _log.ExceptionSummary(ex2), "RunPassAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\shared\\dotnet\\DesktopUpdates\\UpdateService.Pass.cs", 80);
		}
		lock (_sync)
		{
			if (_passRunning)
			{
				EndPassLocked();
			}
		}
	}

	private void EndPassLocked()
	{
		_passRunning = false;
		_userUrgent = false;
		_trigger = UpdateTrigger.Heartbeat;
	}

	private async Task<Step> RunOnceAsync(UpdateTrigger trigger, bool urgent)
	{
		if (_feed == null)
		{
			if (!_disabledLogged)
			{
				_disabledLogged = true;
				_log.LocalOnly("update routine: nothing to install, feed disabled reason=" + _feedDisabledReason, "RunOnceAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\shared\\dotnet\\DesktopUpdates\\UpdateService.Pass.cs", 106);
			}
			SetUserCheckFrom("checking", "error");
			return Step.Done;
		}
		if (!NetworkInterface.GetIsNetworkAvailable())
		{
			SetUserCheckFrom("checking", "error");
			return Step.Offline;
		}
		CancellationToken ct = _life.Token;
		await _op.WaitAsync(ct).ConfigureAwait(continueOnCapturedContext: false);
		UpdateInfo info;
		string version;
		try
		{
			try
			{
				info = await _feed.CheckAsync(ct).ConfigureAwait(continueOnCapturedContext: false);
			}
			catch (Exception ex) when (!ct.IsCancellationRequested)
			{
				ReportFailure(UpdateStage.Check, trigger, null, ex);
				SetUserCheckFrom("checking", "error");
				return Step.Retry;
			}
			if (info == null)
			{
				SetTarget(null);
				SetUserCheckFrom("checking", "up_to_date");
				if (Floor.IsBelowFloor)
				{
					ReportNothingToInstall(trigger);
					return Step.Retry;
				}
				LogOutcome("up_to_date", "update check: up to date version=" + _appVersion);
				return Step.Done;
			}
			version = info.TargetFullRelease.Version.ToString();
			SetTarget(version);
			SetUserCheckFrom("checking", "idle");
			if (_feed.IsStaged(info))
			{
				_record.NoteStaged(version, trigger, landedNow: false);
			}
			else if (!(await DownloadAsync(info, version, trigger, ct).ConfigureAwait(continueOnCapturedContext: false)))
			{
				return Step.Retry;
			}
		}
		finally
		{
			_op.Release();
		}
		return await InstallAsync(info, version, trigger, urgent).ConfigureAwait(continueOnCapturedContext: false);
	}

	private async Task WaitAsync(TimeSpan delay)
	{
		await _wake.WaitAsync(delay, _life.Token).ConfigureAwait(continueOnCapturedContext: false);
	}

	private void SignalWake()
	{
		try
		{
			_wake.Release();
		}
		catch (SemaphoreFullException)
		{
		}
		catch (ObjectDisposedException)
		{
		}
	}

	private void SetTarget(string? version)
	{
		if (!(_target == version))
		{
			_target = version;
			RaiseChanged();
		}
	}

	private void SetUserCheckFrom(string from, string to)
	{
		if (!(_userCheck != from))
		{
			_userCheck = to;
			RaiseChanged();
		}
	}

	private void RaiseChanged()
	{
		try
		{
			Changed?.Invoke();
		}
		catch (Exception ex)
		{
			_log.LocalOnly("update state subscriber failed: " + _log.ExceptionSummary(ex), "RaiseChanged", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\shared\\dotnet\\DesktopUpdates\\UpdateService.State.cs", 38);
		}
	}

	private void CountAttemptOnce(string version)
	{
		bool flag;
		lock (_sync)
		{
			flag = _attemptCounted.Add(version);
		}
		if (flag)
		{
			Count.Upgrade(UpdateOutcome.Attempt, 1L);
		}
	}

	private void LogOutcome(string key, string line)
	{
		lock (_sync)
		{
			if (_lastOutcome == key)
			{
				return;
			}
			_lastOutcome = key;
		}
		_log.Update(line, "LogOutcome", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\shared\\dotnet\\DesktopUpdates\\UpdateService.State.cs", 69);
	}

	private void ReportNothingToInstall(UpdateTrigger trigger)
	{
		bool flag;
		lock (_sync)
		{
			flag = !_nothingToInstallReported;
			_nothingToInstallReported = true;
		}
		LogOutcome("nothing_to_install", $"update check: below the floor min_version={Floor.MinVersion ?? "-"} and the feed has nothing above version={_appVersion}; retrying");
		if (flag)
		{
			Count.Upgrade(UpdateOutcome.Fail, 1L);
			Telemetry.Emit(Ev.UpdateFailed(UpdateStage.Check, "no_update_on_feed", "below the minimum " + (Floor.MinVersion ?? "-") + " and the feed has nothing above " + _appVersion, trigger, NextAttempt(UpdateStage.Check, null)));
		}
	}

	private void ReportFailure(UpdateStage stage, UpdateTrigger trigger, string? version, Exception ex)
	{
		int num = NextAttempt(stage, version);
		string msg = $"update failed stage={stage.ToWire()} version={version ?? "-"} trigger={trigger.ToWire()} attempt={num}: {_log.ExceptionSummary(ex)}";
		Count.Upgrade(UpdateOutcome.Fail, 1L);
		if (!ReportsAttempt(num))
		{
			_log.LocalOnly(msg, "ReportFailure", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\shared\\dotnet\\DesktopUpdates\\UpdateService.State.cs", 103);
			return;
		}
		_log.Update(msg, "ReportFailure", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\shared\\dotnet\\DesktopUpdates\\UpdateService.State.cs", 107);
		Telemetry.Emit(Ev.UpdateFailed(stage, ex.GetType().Name, ex.Message, trigger, num));
	}

	private int NextAttempt(UpdateStage stage, string? version)
	{
		string key = stage.ToWire() + ":" + (version ?? "-");
		lock (_sync)
		{
			_attempts.TryGetValue(key, out var value);
			return _attempts[key] = value + 1;
		}
	}

	private static int Rank(UpdateTrigger trigger)
	{
		return trigger switch
		{
			UpdateTrigger.Required => 4, 
			UpdateTrigger.User => 3, 
			UpdateTrigger.Launch => 2, 
			UpdateTrigger.Heartbeat => 1, 
			_ => 0, 
		};
	}

	private static UpdateTrigger Stronger(UpdateTrigger a, UpdateTrigger b)
	{
		if (Rank(b) <= Rank(a))
		{
			return a;
		}
		return b;
	}
}
