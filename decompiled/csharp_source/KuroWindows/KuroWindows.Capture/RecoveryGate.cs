using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using ClientTelemetry;
using KuroWindows.Input;
using KuroWindows.RecorderCore.Clock;
using KuroWindows.Sync;

namespace KuroWindows.Capture;

internal sealed class RecoveryGate
{
	internal const int LockedHoldPollSeconds = 300;

	internal const int LockedHoldShortPollSeconds = 60;

	internal const int RemoteHoldPollSeconds = 300;

	internal const int StructuralDeadPacingSeconds = 1800;

	internal const int ChronicPacingSeconds = 600;

	internal const int RemoteExhaustedPacingSeconds = 900;

	internal const int FastRetryWindowMs = 30000;

	internal const int FastRetryClampSeconds = 3;

	internal const int OsEventBurstCount = 3;

	internal const int OsEventBurstWindowMs = 120000;

	internal const int OsEventBurstSpacingMs = 25000;

	internal const int OsEventBurstSettleMs = 60000;

	internal const int PacingInputPollSeconds = 60;

	private static readonly int[] AccessDeniedLadderSeconds = new int[3] { 3, 10, 30 };

	private const string ClassTransient = "transient";

	private const string ClassAccessDenied = "access_denied";

	private const string ClassAccessDeniedLocked = "access_denied_locked";

	private const string ClassRemoteDisconnected = "remote_disconnected";

	private const string ClassStructuralDead = "structural_dead";

	private const string ClassChronic = "chronic";

	private const string ClassRemoteExhausted = "remote_exhausted";

	private const string ClassEventBurst = "event_burst";

	private const string GateLadder = "ladder";

	private const string GateHold = "hold";

	private const string GatePacing = "pacing";

	private readonly Func<RecoveryStartInitiator, RecoveryStartDisposition> _executeStart;

	private readonly Func<SessionState> _state;

	private readonly Func<bool> _startWanted;

	private readonly Func<bool> _tryClearNonIdleStateForRecovery;

	private readonly Action _markAdaptiveStable;

	private readonly Func<IReadOnlyList<string>> _displayKeys;

	private readonly Func<SessionEnvironmentKind> _environmentKind;

	private readonly Func<IReadOnlyList<string>, SessionEnvironmentKind, SessionPacingClass> _derivePacing;

	private readonly Func<bool> _queryWtsInteractive;

	private readonly Func<bool> _sessionInteractive;

	private readonly Func<long> _lastEventObservedUtcMs;

	private readonly Func<DesktopCaptureRecoveryFlags> _flags;

	private readonly Func<bool> _shutdownLatched;

	private readonly Func<long> _lastInputAgeMs;

	private readonly Action _clearFirstUserInputEvidence;

	private readonly Func<long> _nowMs;

	private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;

	private readonly object _sync = new object();

	private int _failureStreak;

	private CancellationTokenSource? _retryCts;

	private long _scheduleGeneration;

	private string _holdClass = "";

	private string _holdGateKind = "";

	private long _nextEligibleAtMs;

	private long _pacingParkArmedAtMs;

	private string _lastSuppressedClass = "";

	private long _lastOsEventAtMs;

	private int _fastRetryArmed;

	private readonly List<long> _osEventStartsMs = new List<long>();

	private volatile bool _pollShortenedForLifetime;

	private int _eventHealthDegradedLogged;

	private volatile string _lastAdmittedInitiator = "-";

	private RecoveryStartDisposition _lastDisposition;

	private System.Timers.Timer? _settleTimer;

	private long _unavailableSinceMs;

	private long _cumulativeUnavailableMs;

	private volatile string _unavailableClass = "";

	public int FailureStreak => Volatile.Read(in _failureStreak);

	public bool HasFailureState
	{
		get
		{
			if (Volatile.Read(in _failureStreak) > 0)
			{
				return true;
			}
			lock (_sync)
			{
				return _holdClass.Length != 0 || _retryCts != null;
			}
		}
	}

	internal string HoldClassForTests
	{
		get
		{
			lock (_sync)
			{
				return _holdClass;
			}
		}
	}

	internal string HoldGateKindForTests
	{
		get
		{
			lock (_sync)
			{
				return _holdGateKind;
			}
		}
	}

	internal bool PendingRetryActiveForTests
	{
		get
		{
			lock (_sync)
			{
				return _retryCts != null;
			}
		}
	}

	internal long NextEligibleInMsForTests
	{
		get
		{
			lock (_sync)
			{
				return (_holdClass.Length == 0) ? 0 : Math.Max(0L, _nextEligibleAtMs - _nowMs());
			}
		}
	}

	internal bool PollShortenedForTests => _pollShortenedForLifetime;

	internal RecoveryStartDisposition LastDispositionForTests => _lastDisposition;

	internal string LastAdmittedInitiatorForTests => _lastAdmittedInitiator;

	public RecoveryGate(Func<RecoveryStartInitiator, RecoveryStartDisposition> executeStart, Func<SessionState> state, Func<bool> startWanted, Func<bool> tryClearNonIdleStateForRecovery, Action markAdaptiveStable, Func<IReadOnlyList<string>> displayKeys, Func<SessionEnvironmentKind> environmentKind, Func<IReadOnlyList<string>, SessionEnvironmentKind, SessionPacingClass> derivePacing, Func<bool> queryWtsInteractive, Func<bool> sessionInteractive, Func<long> lastEventObservedUtcMs, Func<DesktopCaptureRecoveryFlags> flags, Func<bool> shutdownLatched)
		: this(executeStart, state, startWanted, tryClearNonIdleStateForRecovery, markAdaptiveStable, displayKeys, environmentKind, derivePacing, queryWtsInteractive, sessionInteractive, lastEventObservedUtcMs, flags, shutdownLatched, () => SystemAvailabilityEvents.LastInputAgeMs(), () =>
		{
			CaptureHealthRegistry.ClearForRecoveryEvent(CaptureRecoveryEvent.FirstUserInput);
		}, () => RecordingClock.MonotonicMs(), (TimeSpan delay, CancellationToken token) => Task.Delay(delay, token))
	{
	}

	internal RecoveryGate(Func<RecoveryStartInitiator, RecoveryStartDisposition> executeStart, Func<SessionState> state, Func<bool> startWanted, Func<bool> tryClearNonIdleStateForRecovery, Action markAdaptiveStable, Func<IReadOnlyList<string>> displayKeys, Func<SessionEnvironmentKind> environmentKind, Func<IReadOnlyList<string>, SessionEnvironmentKind, SessionPacingClass> derivePacing, Func<bool> queryWtsInteractive, Func<bool> sessionInteractive, Func<long> lastEventObservedUtcMs, Func<DesktopCaptureRecoveryFlags> flags, Func<bool> shutdownLatched, Func<long> lastInputAgeMs, Action clearFirstUserInputEvidence, Func<long> nowMs, Func<TimeSpan, CancellationToken, Task> delayAsync)
	{
		_executeStart = executeStart;
		_state = state;
		_startWanted = startWanted;
		_tryClearNonIdleStateForRecovery = tryClearNonIdleStateForRecovery;
		_markAdaptiveStable = markAdaptiveStable;
		_displayKeys = displayKeys;
		_environmentKind = environmentKind;
		_derivePacing = derivePacing;
		_queryWtsInteractive = queryWtsInteractive;
		_sessionInteractive = sessionInteractive;
		_lastEventObservedUtcMs = lastEventObservedUtcMs;
		_flags = flags;
		_shutdownLatched = shutdownLatched;
		_lastInputAgeMs = lastInputAgeMs;
		_clearFirstUserInputEvidence = clearFirstUserInputEvidence;
		_nowMs = nowMs;
		_delayAsync = delayAsync;
	}

	public void ResetFailureStreak()
	{
		Interlocked.Exchange(ref _failureStreak, 0);
	}

	public void RequestStart(RecoveryStartInitiator initiator)
	{
		if (IsUserIntent(initiator))
		{
			AdmitStart(initiator);
			return;
		}
		if (initiator == RecoveryStartInitiator.PendingQueue)
		{
			AdmitStart(initiator);
			return;
		}
		if (_flags().LegacyCadence)
		{
			if (initiator != RecoveryStartInitiator.OsEvent)
			{
				AdmitStart(initiator);
			}
			return;
		}
		lock (_sync)
		{
			long num = _nowMs();
			if (initiator == RecoveryStartInitiator.OsEvent && OsEventBurstThrottledNoLock(num, out var waitSeconds))
			{
				SuppressNoLock(initiator, "event_burst", waitSeconds);
				if (_retryCts == null && _holdClass.Length == 0 && _startWanted())
				{
					int num2 = (int)Math.Max(1L, waitSeconds);
					int num3 = Volatile.Read(in _failureStreak);
					_holdClass = "event_burst";
					_holdGateKind = "ladder";
					_nextEligibleAtMs = num + (long)num2 * 1000L;
					KuroLog.Capture($"capture_recovery scheduled class={"event_burst"} attempt={num3} delay_s={num2} gate={"ladder"}", "RequestStart", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\RecoveryGate.cs", 330);
					EmitRecoveryScheduled("event_burst", num3, num2, "ladder");
					StartScheduleTimerNoLock("event_burst", "ladder", num3, num2);
				}
				return;
			}
			if (_holdClass.Length != 0)
			{
				long num4 = _nextEligibleAtMs - num;
				if (num4 > 0)
				{
					SuppressNoLock(initiator, _holdClass, (num4 + 999) / 1000);
					return;
				}
			}
		}
		AdmitStart(initiator);
	}

	private void AdmitStart(RecoveryStartInitiator initiator)
	{
		lock (_sync)
		{
			CancelPendingRetryNoLock();
			ClearHoldNoLock();
			_lastSuppressedClass = "";
			_lastAdmittedInitiator = InitiatorName(initiator);
			if (initiator == RecoveryStartInitiator.OsEvent)
			{
				_osEventStartsMs.Add(_nowMs());
			}
		}
		_lastDisposition = _executeStart(initiator);
	}

	public void RecordFailure(Exception ex)
	{
		if (_shutdownLatched())
		{
			KuroLog.LocalOnly("capture", "Capture recovery not armed: app shutdown in progress", "RecordFailure", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\RecoveryGate.cs", 392);
			return;
		}
		if (!_startWanted())
		{
			KuroLog.Capture("Capture recovery skipped: not signed in or recording disabled", "RecordFailure", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\RecoveryGate.cs", 397);
			return;
		}
		if (_state() == SessionState.UpdateRequired)
		{
			KuroLog.Capture("Capture recovery skipped: update is required", "RecordFailure", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\RecoveryGate.cs", 402);
			return;
		}
		int num = Interlocked.Increment(ref _failureStreak);
		DesktopCaptureRecoveryFlags desktopCaptureRecoveryFlags = _flags();
		string text;
		string text2;
		int num2;
		if (desktopCaptureRecoveryFlags.LegacyCadence)
		{
			text = "transient";
			text2 = "ladder";
			num2 = LadderDelay(AppConfig.CaptureRetryDelaysSeconds, num);
		}
		else
		{
			(text, text2, num2) = ClassifyFailure(ex, num, desktopCaptureRecoveryFlags);
			if (text2 == "hold" && _lastEventObservedUtcMs() == 0L)
			{
				if (Interlocked.Exchange(ref _eventHealthDegradedLogged, 1) == 0)
				{
					KuroLog.Capture("capture_recovery degraded class=" + text + " reason=no_os_events_observed; using transient ladder", "RecordFailure", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\RecoveryGate.cs", 430);
				}
				text = "transient";
				text2 = "ladder";
				num2 = LadderDelay(AppConfig.CaptureRetryDelaysSeconds, num);
			}
			if (text2 != "hold")
			{
				long num3 = Volatile.Read(in _lastOsEventAtMs);
				if (num3 > 0 && _nowMs() - num3 <= 30000 && Interlocked.Exchange(ref _fastRetryArmed, 0) == 1)
				{
					num2 = Math.Min(num2, 3);
				}
			}
		}
		lock (_sync)
		{
			CancelPendingRetryNoLock();
			_holdClass = text;
			_holdGateKind = text2;
			_lastSuppressedClass = "";
			long num4 = _nowMs();
			_nextEligibleAtMs = num4 + (long)num2 * 1000L;
			KuroLog.Capture($"capture_recovery scheduled class={text} attempt={num} delay_s={num2} gate={text2}", "RecordFailure", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\RecoveryGate.cs", 459);
			EmitRecoveryScheduled(text, num, num2, text2);
			int delaySeconds = num2;
			if (IsInputWakeablePacing(text, text2))
			{
				_pacingParkArmedAtMs = num4;
				delaySeconds = Math.Min(60, num2);
			}
			StartScheduleTimerNoLock(text, text2, num, delaySeconds);
		}
	}

	private static bool IsInputWakeablePacing(string cls, string gateKind)
	{
		bool flag = gateKind == "pacing";
		if (flag)
		{
			bool flag2 = ((cls == "chronic" || cls == "structural_dead") ? true : false);
			flag = flag2;
		}
		return flag;
	}

	private (string Cls, string GateKind, int DelaySeconds) ClassifyFailure(Exception ex, int attempt, DesktopCaptureRecoveryFlags flags)
	{
		if (CaptureAccessDeniedClassifier.IsAccessDenied(ex))
		{
			if (!_sessionInteractive())
			{
				return (Cls: "access_denied_locked", GateKind: "hold", DelaySeconds: LockedPollSeconds());
			}
			return (Cls: "access_denied", GateKind: "ladder", DelaySeconds: LadderDelay(AccessDeniedLadderSeconds, attempt));
		}
		SessionEnvironmentKind sessionEnvironmentKind = _environmentKind();
		string effectiveRemotePolicy = flags.EffectiveRemotePolicy;
		if (sessionEnvironmentKind == SessionEnvironmentKind.RemoteDisconnected && effectiveRemotePolicy != "off")
		{
			return (Cls: "remote_disconnected", GateKind: "hold", DelaySeconds: 300);
		}
		switch (_derivePacing(_displayKeys(), sessionEnvironmentKind))
		{
		case SessionPacingClass.StructuralDead:
			return (Cls: "structural_dead", GateKind: "pacing", DelaySeconds: 1800);
		case SessionPacingClass.Chronic:
			return (Cls: "chronic", GateKind: "pacing", DelaySeconds: 600);
		case SessionPacingClass.RemoteExhausted:
			if (effectiveRemotePolicy == "full")
			{
				return (Cls: "remote_exhausted", GateKind: "pacing", DelaySeconds: 900);
			}
			break;
		}
		return (Cls: "transient", GateKind: "ladder", DelaySeconds: LadderDelay(AppConfig.CaptureRetryDelaysSeconds, attempt));
	}

	private static void EmitRecoveryScheduled(string cls, int attempt, int delaySeconds, string gateKind)
	{
		Count.CaptureRecovery(TelemetryMap.RecoveryAttemptBucket(attempt), 1L);
		Telemetry.Emit(Ev.CaptureRecoveryScheduled(cls, attempt, delaySeconds, gateKind));
	}

	private void StartScheduleTimerNoLock(string cls, string gateKind, int attempt, int delaySeconds)
	{
		CancelPendingRetryNoLock();
		long generation = ++_scheduleGeneration;
		CancellationToken token = (_retryCts = new CancellationTokenSource()).Token;
		Task.Run(async () =>
		{
			try
			{
				await _delayAsync(TimeSpan.FromSeconds(delaySeconds), token).ConfigureAwait(continueOnCapturedContext: false);
			}
			catch (OperationCanceledException)
			{
				return;
			}
			if (token.IsCancellationRequested)
			{
				return;
			}
			try
			{
				OnScheduleDue(generation, cls, gateKind, attempt);
			}
			catch (Exception ex2)
			{
				KuroLog.Capture("Capture recovery StartSession failed: " + KuroLog.ExceptionSummary(ex2), "StartScheduleTimerNoLock", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\RecoveryGate.cs", 553);
			}
		});
	}

	private bool ScheduleGenerationIsCurrent(long generation)
	{
		lock (_sync)
		{
			return generation == _scheduleGeneration;
		}
	}

	private void OnScheduleDue(long generation, string cls, string gateKind, int attempt)
	{
		if (!ScheduleGenerationIsCurrent(generation))
		{
			return;
		}
		if (_shutdownLatched())
		{
			KuroLog.LocalOnly("capture", "Capture recovery retry dropped: app shutdown in progress", "OnScheduleDue", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\RecoveryGate.cs", 585);
			lock (_sync)
			{
				if (generation == _scheduleGeneration)
				{
					ClearHoldNoLock();
				}
				return;
			}
		}
		if (!_startWanted())
		{
			KuroLog.Capture("Capture recovery ended while signed out or recording disabled", "OnScheduleDue", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\RecoveryGate.cs", 598);
			lock (_sync)
			{
				if (generation == _scheduleGeneration)
				{
					ClearHoldNoLock();
				}
				return;
			}
		}
		if (gateKind == "hold")
		{
			if (cls == "access_denied_locked")
			{
				if (!_queryWtsInteractive())
				{
					RearmHold(generation, cls, attempt);
					return;
				}
				if (!ScheduleGenerationIsCurrent(generation))
				{
					return;
				}
				_pollShortenedForLifetime = true;
				KuroLog.Capture($"os_recovery event=missed_unlock action=start state={_state()}", "OnScheduleDue", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\RecoveryGate.cs", 634);
			}
			else
			{
				if (_environmentKind() == SessionEnvironmentKind.RemoteDisconnected)
				{
					RearmHold(generation, cls, attempt);
					return;
				}
				if (!ScheduleGenerationIsCurrent(generation))
				{
					return;
				}
				KuroLog.Capture($"os_recovery event=remote_reattach action=start state={_state()}", "OnScheduleDue", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\RecoveryGate.cs", 649);
			}
		}
		if (IsInputWakeablePacing(cls, gateKind))
		{
			bool flag;
			lock (_sync)
			{
				if (generation != _scheduleGeneration || _holdClass != cls)
				{
					return;
				}
				long num = _nowMs();
				long num2 = _nextEligibleAtMs - num;
				long num3 = _lastInputAgeMs();
				flag = num3 >= 0 && num3 < num - _pacingParkArmedAtMs;
				if (!flag && num2 > 0)
				{
					int delaySeconds = (int)Math.Min(60L, (num2 + 999) / 1000);
					StartScheduleTimerNoLock(cls, gateKind, attempt, delaySeconds);
					return;
				}
			}
			if (flag)
			{
				_clearFirstUserInputEvidence();
				KuroLog.Capture($"os_recovery event=first_user_input action=start state={_state()}", "OnScheduleDue", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\RecoveryGate.cs", 689);
			}
		}
		if (_tryClearNonIdleStateForRecovery())
		{
			KuroLog.Capture($"Capture recovery starting session attempt #{attempt}", "OnScheduleDue", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\RecoveryGate.cs", 700);
			AdmitStart(RecoveryStartInitiator.LadderRetry);
		}
	}

	private void RearmHold(long generation, string cls, int attempt)
	{
		lock (_sync)
		{
			if (generation == _scheduleGeneration && !(_holdClass != cls))
			{
				int num = ((cls == "access_denied_locked") ? LockedPollSeconds() : 300);
				_nextEligibleAtMs = _nowMs() + (long)num * 1000L;
				StartScheduleTimerNoLock(cls, "hold", attempt, num);
			}
		}
	}

	private int LockedPollSeconds()
	{
		if (!_pollShortenedForLifetime)
		{
			return 300;
		}
		return 60;
	}

	private static int LadderDelay(int[] delays, int attempt)
	{
		return delays[Math.Min(Math.Max(attempt, 1) - 1, delays.Length - 1)];
	}

	public void NoteOsEvent()
	{
		if (!_flags().LegacyCadence)
		{
			Volatile.Write(ref _lastOsEventAtMs, _nowMs());
			Volatile.Write(ref _fastRetryArmed, 1);
		}
	}

	public void ClearHoldsForOsEvent(RecoveryOsEvent osEvent)
	{
		if (_flags().LegacyCadence)
		{
			return;
		}
		lock (_sync)
		{
			if (_holdClass.Length == 0 && _retryCts == null)
			{
				return;
			}
			bool flag2;
			bool flag;
			switch (osEvent)
			{
			case RecoveryOsEvent.Unlock:
			case RecoveryOsEvent.Logon:
			{
				string holdClass = _holdClass;
				if (holdClass == null || holdClass.Length != 0)
				{
					switch (holdClass)
					{
					case "transient":
					case "access_denied":
					case "access_denied_locked":
					case "chronic":
					case "remote_exhausted":
						break;
					default:
						goto IL_00b1;
					}
				}
				flag2 = true;
				goto IL_00b4;
			}
			case RecoveryOsEvent.Connect:
			{
				string holdClass = _holdClass;
				if (holdClass == null || holdClass.Length != 0)
				{
					switch (holdClass)
					{
					case "transient":
					case "remote_disconnected":
					case "chronic":
					case "remote_exhausted":
						break;
					default:
						goto IL_0107;
					}
				}
				flag2 = true;
				goto IL_010a;
			}
			case RecoveryOsEvent.Resume:
			{
				string holdClass = _holdClass;
				if (holdClass == null || holdClass.Length != 0)
				{
					switch (holdClass)
					{
					case "transient":
					case "chronic":
					case "remote_exhausted":
						break;
					default:
						goto IL_014d;
					}
				}
				flag2 = true;
				goto IL_0150;
			}
			default:
				{
					flag = false;
					break;
				}
				IL_010a:
				flag = flag2;
				break;
				IL_014d:
				flag2 = false;
				goto IL_0150;
				IL_0107:
				flag2 = false;
				goto IL_010a;
				IL_0150:
				flag = flag2;
				break;
				IL_00b1:
				flag2 = false;
				goto IL_00b4;
				IL_00b4:
				flag = flag2;
				break;
			}
			if (flag)
			{
				CancelPendingRetryNoLock();
				ClearHoldNoLock();
			}
		}
	}

	public void ClearHoldsForTopologyChange()
	{
		lock (_sync)
		{
			bool flag;
			switch (_holdClass)
			{
			case "structural_dead":
			case "chronic":
			case "remote_exhausted":
				flag = true;
				break;
			default:
				flag = false;
				break;
			}
			if (flag)
			{
				CancelPendingRetryNoLock();
				ClearHoldNoLock();
			}
		}
	}

	private bool OsEventBurstThrottledNoLock(long now, out long waitSeconds)
	{
		waitSeconds = 0L;
		if (_osEventStartsMs.Count == 0)
		{
			return false;
		}
		List<long> osEventStartsMs = _osEventStartsMs;
		long num = osEventStartsMs[osEventStartsMs.Count - 1];
		if (now - num >= 60000)
		{
			_osEventStartsMs.Clear();
			return false;
		}
		_osEventStartsMs.RemoveAll((long at) => now - at > 120000);
		if (_osEventStartsMs.Count >= 3 && now - num < 25000)
		{
			waitSeconds = (25000 - (now - num) + 999) / 1000;
			return true;
		}
		return false;
	}

	public void CancelPendingRetry()
	{
		lock (_sync)
		{
			CancelPendingRetryNoLock();
			ClearHoldNoLock();
		}
	}

	private void CancelPendingRetryNoLock()
	{
		_scheduleGeneration++;
		CancellationTokenSource retryCts = _retryCts;
		_retryCts = null;
		if (retryCts != null)
		{
			try
			{
				retryCts.Cancel();
			}
			catch
			{
			}
			retryCts.Dispose();
		}
	}

	private void ClearHoldNoLock()
	{
		_holdClass = "";
		_holdGateKind = "";
		_nextEligibleAtMs = 0L;
		_lastSuppressedClass = "";
	}

	private void SuppressNoLock(RecoveryStartInitiator initiator, string cls, long nextEligibleInS)
	{
		if (!(_lastSuppressedClass == cls))
		{
			_lastSuppressedClass = cls;
			KuroLog.Capture($"capture_recovery suppressed initiator={InitiatorName(initiator)} class={cls} next_eligible_in_s={nextEligibleInS}", "SuppressNoLock", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\RecoveryGate.cs", 886);
		}
	}

	public void OpenUnavailabilityInterval(string cls, Exception ex)
	{
		if (Interlocked.CompareExchange(ref _unavailableSinceMs, _nowMs(), 0L) == 0L)
		{
			_unavailableClass = cls;
			Telemetry.Emit(Ev.CaptureAvailabilityChanged(AvailabilityState.Unavailable, cls));
			KuroLog.Capture($"capture_availability state=unavailable class={cls} hr=0x{CaptureAccessDeniedClassifier.LoggableHResult(ex):X8} remote_session={RemoteSessionProbe.LogValue} session_state={RemoteSessionProbe.SessionStateLogValue} inner_hrs={CaptureAccessDeniedClassifier.InnerHResultsLogValue(ex)}", "OpenUnavailabilityInterval", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\RecoveryGate.cs", 908);
		}
	}

	public void CloseUnavailabilityInterval(string state)
	{
		long num = Interlocked.Exchange(ref _unavailableSinceMs, 0L);
		if (num != 0L)
		{
			long value = Math.Max(0L, _nowMs() - num);
			long value2 = Interlocked.Add(ref _cumulativeUnavailableMs, value);
			if (state == "recovered")
			{
				Telemetry.Emit(Ev.CaptureRecovered(Volatile.Read(in _failureStreak), value));
			}
			else
			{
				Telemetry.Emit(Ev.CaptureAvailabilityChanged(AvailabilityState.Abandoned, _unavailableClass));
			}
			KuroLog.Capture($"capture_availability state={state} class={_unavailableClass} unavailable_ms={value} cumulative_unavailable_ms={value2} attempt={Volatile.Read(in _failureStreak)} remote_session={RemoteSessionProbe.LogValue} initiator={_lastAdmittedInitiator} session_state={RemoteSessionProbe.SessionStateLogValue}", "CloseUnavailabilityInterval", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\RecoveryGate.cs", 944);
		}
	}

	public void ArmSettleTimer()
	{
		CloseUnavailabilityInterval("recovered");
		StopSettleTimer();
		System.Timers.Timer timer = new System.Timers.Timer(AppConfig.CaptureRetrySettleSeconds * 1000)
		{
			AutoReset = false
		};
		timer.Elapsed += (object? _, ElapsedEventArgs _) =>
		{
			try
			{
				if (Interlocked.CompareExchange(ref _settleTimer, null, timer) != timer)
				{
					return;
				}
				SessionState sessionState = _state();
				if ((uint)(sessionState - 1) <= 2u)
				{
					int num = Interlocked.Exchange(ref _failureStreak, 0);
					if (num > 0)
					{
						KuroLog.Capture($"Capture recovery settled after {AppConfig.CaptureRetrySettleSeconds}s; counter cleared (was {num})", "ArmSettleTimer", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\RecoveryGate.cs", 987);
					}
					_markAdaptiveStable();
				}
				try
				{
					timer.Stop();
					timer.Dispose();
				}
				catch
				{
				}
			}
			catch (Exception ex)
			{
				KuroLog.Recording("capture_settle timer callback failed: " + KuroLog.ExceptionSummary(ex), "ArmSettleTimer", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\RecoveryGate.cs", 999);
			}
		};
		System.Timers.Timer timer2 = Interlocked.Exchange(ref _settleTimer, timer);
		if (timer2 != null)
		{
			try
			{
				timer2.Stop();
				timer2.Dispose();
			}
			catch
			{
			}
		}
		timer.Start();
	}

	public void StopSettleTimer()
	{
		System.Timers.Timer timer = Interlocked.Exchange(ref _settleTimer, null);
		if (timer == null)
		{
			return;
		}
		try
		{
			timer.Stop();
			timer.Dispose();
		}
		catch
		{
		}
	}

	private static bool IsUserIntent(RecoveryStartInitiator initiator)
	{
		if ((uint)(initiator - 6) <= 2u)
		{
			return true;
		}
		return false;
	}

	internal static string InitiatorName(RecoveryStartInitiator initiator)
	{
		return initiator switch
		{
			RecoveryStartInitiator.LadderRetry => "ladder_retry", 
			RecoveryStartInitiator.SyncTick => "sync_tick", 
			RecoveryStartInitiator.DisplayChange => "display_change", 
			RecoveryStartInitiator.DiskRecovery => "disk_recovery", 
			RecoveryStartInitiator.PendingQueue => "pending_queue", 
			RecoveryStartInitiator.OsEvent => "os_event", 
			RecoveryStartInitiator.AuthSignin => "auth_signin", 
			RecoveryStartInitiator.Onboarding => "onboarding", 
			RecoveryStartInitiator.CorebridgeManual => "corebridge_manual", 
			_ => "unknown", 
		};
	}
}
