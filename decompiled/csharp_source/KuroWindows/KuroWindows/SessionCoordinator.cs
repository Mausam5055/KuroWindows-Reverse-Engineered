using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using SystemAudio;
using ClientTelemetry;
using KuroWindows.Auth;
using KuroWindows.Capture;
using KuroWindows.Input;
using KuroWindows.RecorderCore.Capture;
using KuroWindows.RecorderCore.Clock;
using KuroWindows.Settings;
using KuroWindows.Storage;
using KuroWindows.Sync;
using KuroWindows.Update;
using KuroWindows.Upload;
using Microsoft.Win32;

namespace KuroWindows;

public class SessionCoordinator
{
	private readonly record struct DetachedRuntime(SegmentSet? Segment, SessionCaptureEngine? Engine, RecorderWriteRouter? Router, UploadManager? UploadManager, string? SessionDir, PrivacyContext? PrivacyContext, ISystemAudio? SystemAudio);

	private readonly AuthManager _auth;

	private readonly UploadManager? _uploadManager;

	private readonly StorageManager _storage = new StorageManager();

	private System.Timers.Timer? _diskCheckTimer;

	private System.Timers.Timer? _clipRotationTimer;

	private System.Timers.Timer? _lockShellRecheckTimer;

	private SessionCaptureEngine? _captureEngine;

	private RecorderWriteRouter? _writeRouter;

	private SegmentSet? _activeSegment;

	private InputMonitor? _inputMonitor;

	private SystemMonitor? _systemMonitor;

	private IdleManager? _idleManager;

	private PrivacyManager? _privacyManager;

	private MicrophoneMonitor? _microphoneMonitor;

	private CameraMonitor? _cameraMonitor;

	private ISystemAudio? _systemAudio;

	private FrameStateSource? _frameState;

	private PrivacyContext? _activePrivacyContext;

	private CancellationTokenSource? _privacyResumeCts;

	private readonly SemaphoreSlim _privacyTransitionGate = new SemaphoreSlim(1, 1);

	private readonly object _writeRoutingSync = new object();

	private readonly object _desktopConfigSync = new object();

	private readonly object _timerSync = new object();

	private DesktopConfig? _desktopConfig;

	private bool _stoppedForLowDisk;

	private bool _sessionStartedRecorded;

	private int _currentSegmentIndex;

	private int _captureFailureHandling;

	private int _rotationInProgress;

	private int _clipRotationTimerGeneration;

	private int _startInProgress;

	private int _stopInProgress;

	private string? _activeStopReason;

	private int _pendingStartAfterCleanup;

	private int _updateRequiredRequested;

	private int _updateInstallHold;

	private TaskCompletionSource<bool>? _updateBoundary;

	private volatile string? _pausedFor;

	private readonly SessionParks _parks = new SessionParks();

	private readonly ConcurrentDictionary<Task, byte> _finalizesInFlight = new ConcurrentDictionary<Task, byte>();

	private volatile bool _appShutdownRequested;

	private readonly SystemAvailabilityEvents _availabilityEvents;

	private readonly RecoveryGate _recoveryGate;

	private readonly CaptureContentionMonitor? _captureContention;

	private static readonly TimeSpan CaptureContentionStartWait = TimeSpan.FromSeconds(2.0);

	private int _contentionMismatchScans;

	private CancellationTokenSource? _resumeSettleCts;

	private bool _resumeInteractiveDeferLogged;

	private bool _parkedRecheckKeptLogged;

	private int _displayChangeHandling;

	private int _displayChangePending;

	private volatile string? _unsupportedTopologyReason;

	private string? _lastUnsupportedLogSignature;

	private readonly AdaptiveCaptureProfileStore _adaptiveProfileStore = new AdaptiveCaptureProfileStore();

	private DisplayTopologyPolicyResult? _activeDisplayPolicy;

	private int _state;

	private const double FailedBoundaryRotationDelayMs = 1000.0;

	private static readonly TimeSpan UpdateInstallSealWait = TimeSpan.FromSeconds(90.0);

	public SessionState State
	{
		get
		{
			return (SessionState)Volatile.Read(in _state);
		}
		private set
		{
			Volatile.Write(ref _state, (int)value);
		}
	}

	public string? CurrentSessionDir { get; private set; }

	public int CurrentSegmentIndex => _currentSegmentIndex;

	public int? ActiveSegmentIndex
	{
		get
		{
			lock (_writeRoutingSync)
			{
				SegmentSet? activeSegment = _activeSegment;
				return (activeSegment != null) ? new int?(activeSegment.SegmentIndex) : _writeRouter?.ActiveSegmentIndex;
			}
		}
	}

	public PrivacyContext? ActivePrivacyContext => _activePrivacyContext;

	public string? UnsupportedTopologyReason => _unsupportedTopologyReason;

	public bool RecordingExpected => ShouldRecordNow();

	public bool ManualRecordingControlsAllowed
	{
		get
		{
			if (_auth.IsSignedIn)
			{
				return !UpdateHoldsRecording;
			}
			return false;
		}
	}

	private bool PausedForSleepOrLogoff
	{
		get
		{
			bool flag = State == SessionState.Paused;
			if (flag)
			{
				string pausedFor = _pausedFor;
				bool flag2 = ((pausedFor == "system_sleep" || pausedFor == "session_logoff") ? true : false);
				flag = flag2 || _availabilityEvents.SuspendFenceArmed || _availabilityEvents.SuspendLatched;
			}
			return flag;
		}
	}

	private bool UpdateHoldsRecording
	{
		get
		{
			if (Volatile.Read(in _updateRequiredRequested) == 0 && Volatile.Read(in _updateInstallHold) == 0)
			{
				return AppUpdates.IsBelowFloor;
			}
			return true;
		}
	}

	private bool CaptureContended => _captureContention?.IsContended ?? false;

	public event Action<SessionState>? StateChanged;

	public event Action? RecordingControlChanged;

	public SessionCoordinator(AuthManager auth, SystemAvailabilityEvents availabilityEvents, UploadManager? uploadManager = null, CaptureContentionMonitor? captureContention = null)
	{
		_auth = auth;
		_availabilityEvents = availabilityEvents;
		_uploadManager = uploadManager;
		_captureContention = captureContention;
		_recoveryGate = new RecoveryGate(StartSessionAdmitted, () => State, () => _auth.IsSignedIn && ShouldRecordNow(), TryClearNonIdleStateForRecovery, MarkAdaptiveProfileStable, CurrentDisplayKeys, () => RemoteSessionProbe.Kind, CaptureHealthRegistry.DerivePacing, SystemAvailabilityEvents.QueryWtsSessionInteractive, () => _availabilityEvents.SessionInteractive, () => _availabilityEvents.LastEventObservedUtcMs, CurrentCaptureRecoveryFlags, () => _appShutdownRequested);
		RemoteSessionProbe.BumpEnvironmentEpoch();
		SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
		_availabilityEvents.SessionBecameActive += OnAvailabilitySessionBecameActive;
		_availabilityEvents.Resumed += OnAvailabilityResumed;
		_availabilityEvents.SessionBecameInactiveGate += (SessionInactiveReason reason) =>
		{
			_inputMonitor?.SetSessionInteractive(interactive: false, reason);
		};
		_availabilityEvents.SessionBecameActiveGate += (SessionActiveReason _) =>
		{
			_inputMonitor?.SetSessionInteractive(interactive: true);
		};
		_availabilityEvents.ResumedGate += (bool _) =>
		{
			_inputMonitor?.Touchpad?.RequestSettingsReread("resume");
		};
		if (_captureContention != null)
		{
			_captureContention.Changed += (bool contended) =>
			{
				_privacyManager?.CaptureContentionChanged(contended);
			};
			_captureContention.Scanned += OnCaptureContentionScanned;
		}
	}

	private void OnCaptureContentionScanned(bool contended)
	{
		SessionState state = State;
		bool flag = state == SessionState.PrivacyPaused && _activePrivacyContext?.Type == "capture_contention";
		if (!(contended ? (state == SessionState.Recording) : flag) || Volatile.Read(in _startInProgress) != 0 || Volatile.Read(in _stopInProgress) != 0 || Volatile.Read(in _captureFailureHandling) != 0)
		{
			_contentionMismatchScans = 0;
		}
		else
		{
			if (++_contentionMismatchScans < (contended ? 2 : 4))
			{
				return;
			}
			_contentionMismatchScans = 0;
			if (contended)
			{
				PrivacyContext privacyContext = _privacyManager?.PrimeCaptureContention(isCaptureContended: true);
				if (privacyContext != null)
				{
					Log("capture contention recheck: recording while contended; closing the write gate", "OnCaptureContentionScanned", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\SessionCoordinator.cs", 287);
					HandlePrivacyStartAsync(privacyContext);
				}
			}
			else
			{
				Log("capture contention recheck: parked on contention with nothing matching; rechecking", "OnCaptureContentionScanned", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\SessionCoordinator.cs", 293);
				RecheckParkedPrivacyContext();
			}
		}
	}

	internal void RequestStart(RecoveryStartInitiator initiator)
	{
		if ((uint)(initiator - 6) <= 2u)
		{
			CaptureHealthRegistry.ClearForRecoveryEvent(CaptureRecoveryEvent.FirstUserInput);
			_availabilityEvents.ClearSuspendHold("user_start");
		}
		_recoveryGate.RequestStart(initiator);
	}

	private RecoveryStartDisposition StartSessionAdmitted(RecoveryStartInitiator initiator)
	{
		if (_appShutdownRequested)
		{
			KuroLog.LocalOnly("recording", "StartSession blocked: app shutdown in progress initiator=" + RecoveryGate.InitiatorName(initiator), "StartSessionAdmitted", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\SessionCoordinator.cs", 328);
			return RecoveryStartDisposition.BlockedGate;
		}
		bool flag = (uint)(initiator - 6) <= 2u;
		bool value = flag;
		if (Interlocked.Exchange(ref _startInProgress, 1) != 0)
		{
			Log($"StartSession coalesced with active start requested_by_user={value}", "StartSessionAdmitted", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\SessionCoordinator.cs", 339);
			return RecoveryStartDisposition.BlockedGate;
		}
		try
		{
			if (State == SessionState.UpdateRequired || UpdateHoldsRecording)
			{
				Log($"StartSession skipped: update is required or installing below_floor={AppUpdates.IsBelowFloor} installing={Volatile.Read(in _updateInstallHold) != 0}", "StartSessionAdmitted", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\SessionCoordinator.cs", 350);
				return RecoveryStartDisposition.BlockedGate;
			}
			if (!_auth.IsSignedIn)
			{
				Log("StartSession skipped: user is not signed in", "StartSessionAdmitted", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\SessionCoordinator.cs", 355);
				return RecoveryStartDisposition.BlockedGate;
			}
			if (!ShouldRecordNow())
			{
				Log("StartSession blocked until local recording gate permits recording", "StartSessionAdmitted", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\SessionCoordinator.cs", 360);
				return RecoveryStartDisposition.BlockedGate;
			}
			if (Volatile.Read(in _stopInProgress) != 0)
			{
				QueueStartAfterCleanup("recorder stop");
				return RecoveryStartDisposition.BlockedGate;
			}
			if (Volatile.Read(in _captureFailureHandling) != 0)
			{
				QueueStartAfterCleanup("capture failure recovery");
				return RecoveryStartDisposition.BlockedGate;
			}
			SessionState state = State;
			if ((uint)(state - 1) <= 2u)
			{
				Log($"StartSession skipped: already in state={State}", "StartSessionAdmitted", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\SessionCoordinator.cs", 375);
				return RecoveryStartDisposition.Started;
			}
			if (State == SessionState.Idle && (CurrentSessionDir != null || _inputMonitor != null || _captureEngine != null || _writeRouter != null || _frameState != null || _systemAudio != null))
			{
				Log("StartSession found partial recorder state; routing through capture recovery before retry", "StartSessionAdmitted", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\SessionCoordinator.cs", 386);
				HandleCaptureFailureAsync(new InvalidOperationException("Partial recorder startup state detected before StartSession."));
				return RecoveryStartDisposition.Failed;
			}
			if (_recoveryGate.FailureStreak == 0)
			{
				_adaptiveProfileStore.ResetForNewRecordingSession();
			}
			DisplayTopologyPolicyResult displayTopologyPolicyResult = EvaluateDisplayPolicyForStart();
			if (!displayTopologyPolicyResult.IsSupported)
			{
				_unsupportedTopologyReason = displayTopologyPolicyResult.UnsupportedReason ?? "unsupported_display_topology";
				if (!string.Equals(_lastUnsupportedLogSignature, displayTopologyPolicyResult.TopologySignature, StringComparison.Ordinal))
				{
					_lastUnsupportedLogSignature = displayTopologyPolicyResult.TopologySignature;
					Log($"StartSession skipped: unsupported display topology reason={displayTopologyPolicyResult.UnsupportedReason} mode={displayTopologyPolicyResult.Mode} physical={displayTopologyPolicyResult.PhysicalDisplayCount} logical={displayTopologyPolicyResult.LogicalDisplayCount} {DisplayPolicyDebug(displayTopologyPolicyResult)}", "StartSessionAdmitted", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\SessionCoordinator.cs", 411);
				}
				SetState(SessionState.Idle);
				return RecoveryStartDisposition.BlockedTopology;
			}
			_unsupportedTopologyReason = null;
			_lastUnsupportedLogSignature = null;
			_activeDisplayPolicy = displayTopologyPolicyResult;
			long num = _storage.AvailableDiskSpaceBytes();
			long num2 = CurrentDiskThresholdBytes();
			if (num < num2)
			{
				_stoppedForLowDisk = true;
				EnsureDiskCheckTimer();
				Log($"StartSession blocked by low disk available_bytes={num} threshold_bytes={num2}", "StartSessionAdmitted", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\SessionCoordinator.cs", 430);
				Count.DiskLow(1L);
				Telemetry.Emit(Ev.DiskLow(num, num2, DiskAction.BlockedStart));
				SetState(SessionState.LowDisk);
				return RecoveryStartDisposition.BlockedLowDisk;
			}
			try
			{
				_stoppedForLowDisk = false;
				CurrentSessionDir = _storage.CreateSessionDirectory();
				_currentSegmentIndex = 0;
				Telemetry.SetContext(Path.GetFileName(CurrentSessionDir), null);
				_frameState = new FrameStateSource();
				_writeRouter = new RecorderWriteRouter
				{
					EventWriteFailed = (Exception ex3) =>
					{
						Task.Run(() => HandleCaptureFailureAsync(ex3));
					},
					SuspendFenceWallMs = () => _availabilityEvents.SuspendFenceWallMs,
					SessionFacts = new SegmentSessionFactsCollector(() => _inputMonitor?.SnapshotAndResetInputFacts()),
					InputPlane = new InputPlaneSource(() => _inputMonitor?.CurrentApp, () => _inputMonitor?.CurrentWindow, () => _inputMonitor?.Channels() ?? default(ChannelCapabilities), () => _inputMonitor?.Touchpad?.Devices, () => _inputMonitor?.Touchpad?.Settings, () => _inputMonitor?.SnapshotHeldState(), (InputStateSnapshot s) =>
					{
						_inputMonitor?.SeedHeldState(s);
					}, () => _inputMonitor?.LastDeliveryQpc() ?? 0, () => _inputMonitor?.LastRearmQpc() ?? 0, () => _inputMonitor?.BlindFlags() ?? default(BlindFlags), () => _inputMonitor?.ProbeSecureDesktop() ?? false, () =>
					{
						_inputMonitor?.PostRearm();
					}, () =>
					{
						_inputMonitor?.PostReconcileButtons();
					}, () => _inputMonitor?.ButtonHeldSinceQpc() ?? 0)
				};
				_activePrivacyContext = null;
				_inputMonitor = new InputMonitor();
				_inputMonitor.SetFrameStateSource(_frameState);
				_inputMonitor.SetEventSink(_writeRouter);
				_inputMonitor.ApplyFeatureFlags(CurrentFeatureFlags());
				_inputMonitor.SetSessionInteractive(SystemAvailabilityEvents.QueryWtsSessionInteractive());
				_inputMonitor.Start();
				_systemMonitor = new SystemMonitor();
				_systemMonitor.SetEventSink(_writeRouter);
				_systemMonitor.SetFrameStateSource(_frameState);
				_systemMonitor.InputSettingsChanged = (string reason) =>
				{
					_inputMonitor?.Touchpad?.RequestSettingsReread(reason);
				};
				_systemMonitor.Start();
				_idleManager = new IdleManager(_availabilityEvents);
				_idleManager.SetEventSink(_writeRouter);
				_idleManager.OnIdle = OnIdle;
				_idleManager.OnResume = OnResume;
				_idleManager.OnInputRefused = _parks.Refused;
				_idleManager.Start();
				_microphoneMonitor = new MicrophoneMonitor();
				_microphoneMonitor.SetEventSink(_writeRouter);
				_microphoneMonitor.ActiveChanged = (bool isActive) =>
				{
					_frameState?.UpdateMicrophoneActive(isActive);
				};
				_microphoneMonitor.Start();
				_cameraMonitor = new CameraMonitor();
				_cameraMonitor.SetEventSink(_writeRouter);
				_cameraMonitor.ActiveChanged = (bool isActive) =>
				{
					_frameState?.UpdateCameraActive(isActive);
					_privacyManager?.CameraActiveChanged(isActive);
				};
				_cameraMonitor.Start();
				_systemAudio = new SystemAudioSession(CurrentSessionDir);
				_privacyManager = new PrivacyManager();
				_privacyManager.OnPrivacyStart = OnPrivacyStart;
				_privacyManager.OnPrivacyEnd = OnPrivacyEnd;
				_privacyManager.ApplyPolicy(CurrentMaskingPolicy());
				_inputMonitor.ForegroundChanged = (ForegroundWindowInfo info) =>
				{
					_privacyManager?.UpdateForeground(info);
				};
				_inputMonitor.WindowTitleChanged = (ForegroundWindowInfo info) =>
				{
					_privacyManager?.TitleDidChange(info);
				};
				_inputMonitor.SecureFieldChanged = (bool isSecure) =>
				{
					_privacyManager?.SecureFieldChanged(isSecure);
				};
				_inputMonitor.OnUserInteraction = (InputKind _) =>
				{
					_idleManager?.UserDidInteract();
				};
				bool isCaptureContended = _captureContention?.WaitForAnswer(CaptureContentionStartWait) ?? false;
				ForegroundWindowInfo currentForegroundInfo = _inputMonitor.GetCurrentForegroundInfo();
				bool currentSecureFieldState = _inputMonitor.GetCurrentSecureFieldState();
				PrivacyContext privacyContext = ((currentForegroundInfo != null) ? _privacyManager.Prime(currentForegroundInfo, currentSecureFieldState, _cameraMonitor.IsActive, isCaptureContended) : _privacyManager.PrimeCaptureContention(isCaptureContended));
				if (!_inputMonitor.WaitForInstall(1000))
				{
					KuroLog.SystemEvent("input hook install not published within 1s; first segment preamble carries the channels published so far", "StartSessionAdmitted", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\SessionCoordinator.cs", 564);
				}
				StartCaptureEngine(displayTopologyPolicyResult);
				if (privacyContext == null && CaptureContended)
				{
					privacyContext = _privacyManager.PrimeCaptureContention(isCaptureContended: true);
				}
				if (privacyContext != null)
				{
					_activePrivacyContext = privacyContext;
					_inputMonitor.SuppressKeys = true;
					Log($"Session started in privacy context trigger={privacyContext.Type} detail={privacyContext.Detail ?? "-"} masked_display={privacyContext.DisplayIndex} title={privacyContext.WindowTitle}", "StartSessionAdmitted", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\SessionCoordinator.cs", 580);
					SetState(SessionState.PrivacyPaused);
					ArmLockShellRecheckIfNeeded(privacyContext);
					EnsureDiskCheckTimer();
					_recoveryGate.ArmSettleTimer();
					RecordSessionStarted();
					return RecoveryStartDisposition.Started;
				}
				if (_idleManager.IsIdle)
				{
					Log("Session started while user/system is already idle; waiting for real input before opening clip", "StartSessionAdmitted", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\SessionCoordinator.cs", 591);
					_pausedFor = null;
					SetState(SessionState.Paused);
					EnsureDiskCheckTimer();
					_recoveryGate.ArmSettleTimer();
					RecordSessionStarted();
					return RecoveryStartDisposition.Started;
				}
				if (UpdateHoldsRecording)
				{
					Log("Session started under an update hold; parked with no clip open", "StartSessionAdmitted", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\SessionCoordinator.cs", 605);
					_pausedFor = "force_update";
					SetState(SessionState.Paused);
					EnsureDiskCheckTimer();
					_recoveryGate.ArmSettleTimer();
					RecordSessionStarted();
					return RecoveryStartDisposition.Started;
				}
				OpenSegment();
				_systemMonitor.LogInitialState();
				SetState(SessionState.Recording);
				StartTimers();
				_recoveryGate.ArmSettleTimer();
				RecordSessionStarted();
				return RecoveryStartDisposition.Started;
			}
			catch (Exception ex)
			{
				Log("StartSession failed during startup: " + KuroLog.ExceptionSummary(ex), "StartSessionAdmitted", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\SessionCoordinator.cs", 626);
				EmitSessionStartFailed(ex);
				HandleCaptureFailureAsync(ex);
				return RecoveryStartDisposition.Failed;
			}
		}
		catch (Exception ex2)
		{
			Log("StartSession failed during startup: " + KuroLog.ExceptionSummary(ex2), "StartSessionAdmitted", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\SessionCoordinator.cs", 637);
			EmitSessionStartFailed(ex2);
			HandleCaptureFailureAsync(ex2);
			return RecoveryStartDisposition.Failed;
		}
		finally
		{
			Interlocked.Exchange(ref _startInProgress, 0);
		}
	}

	private void RecordSessionStarted()
	{
		_sessionStartedRecorded = true;
		Count.SessionStart(1L);
		Telemetry.Emit(Ev.SessionStarted(Path.GetFileName(CurrentSessionDir) ?? "-"));
	}

	private void RecordSessionEnded(SessionEndReason reason, bool cleanStop)
	{
		_parks.SessionEnded();
		if (_sessionStartedRecorded)
		{
			_sessionStartedRecorded = false;
			if (cleanStop)
			{
				Count.SessionCleanStop(1L);
			}
			Telemetry.Emit(Ev.SessionEnded(Path.GetFileName(CurrentSessionDir) ?? "-", reason));
		}
	}

	private static void EmitSessionStartFailed(Exception ex)
	{
		if (!CaptureAccessDeniedClassifier.IsAccessDenied(ex))
		{
			Telemetry.Emit(Ev.SessionStartFailed(SessionStartFailure.CaptureFailed, ex.GetType().Name, ex.Message));
		}
	}

	public void ApplyDesktopConfig(DesktopConfig config)
	{
		DesktopConfig desktopConfig = config.Normalize();
		if (!desktopConfig.TryValidate(out string error))
		{
			throw new InvalidOperationException("Cannot apply invalid desktop config: " + error);
		}
		lock (_desktopConfigSync)
		{
			_desktopConfig = desktopConfig;
		}
		RaiseRecordingControlChanged();
		try
		{
			_privacyManager?.ApplyPolicy(desktopConfig.Masking);
			_inputMonitor?.ApplyFeatureFlags(desktopConfig.FeatureFlags);
			if (State == SessionState.Idle && _auth.IsSignedIn)
			{
				_recoveryGate.RequestStart(RecoveryStartInitiator.SyncTick);
			}
		}
		catch (Exception value)
		{
			Log($"ApplyDesktopConfig failed: {value}", "ApplyDesktopConfig", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\SessionCoordinator.cs", 712);
			throw;
		}
	}

	public void ClearDesktopConfig(string stopReason)
	{
		lock (_desktopConfigSync)
		{
			_desktopConfig = null;
		}
		RaiseRecordingControlChanged();
		try
		{
			_privacyManager?.ApplyPolicy(DesktopMaskingPolicy.Seeded);
		}
		catch (Exception value)
		{
			Log($"ClearDesktopConfig privacy reset failed: {value}", "ClearDesktopConfig", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\SessionCoordinator.cs", 731);
		}
		SessionState state = State;
		if ((uint)(state - 1) <= 2u)
		{
			StopSessionAsync(stopReason);
		}
	}

	private bool ShouldRecordNow()
	{
		if (!UpdateHoldsRecording && _auth.IsSignedIn)
		{
			return UserSettings.Current.RecordingEnabled;
		}
		return false;
	}

	private string StopReasonForCurrentRecordingGate()
	{
		if (UpdateHoldsRecording)
		{
			return "force_update";
		}
		if (UserSettings.Current.RecordingEnabled)
		{
			return "recording_not_expected";
		}
		return "user_stop";
	}

	private DesktopMaskingPolicy CurrentMaskingPolicy()
	{
		lock (_desktopConfigSync)
		{
			return _desktopConfig?.Masking ?? DesktopMaskingPolicy.Seeded;
		}
	}

	private DesktopFeatureFlags CurrentFeatureFlags()
	{
		lock (_desktopConfigSync)
		{
			return _desktopConfig?.FeatureFlags ?? DesktopFeatureFlags.Seeded;
		}
	}

	private DesktopCaptureRecoveryFlags CurrentCaptureRecoveryFlags()
	{
		return CurrentFeatureFlags().CaptureRecovery ?? DesktopCaptureRecoveryFlags.Defaults;
	}

	private long CurrentDiskThresholdBytes()
	{
		return AppConfig.MinimumDiskSpaceBytesForDisplayCount(_captureEngine?.DisplayCount ?? _activeDisplayPolicy?.LogicalDisplayCount ?? Math.Min(MonitorTable.Refresh().Screens.Count, 2));
	}

	private void RaiseRecordingControlChanged()
	{
		try
		{
			RecordingControlChanged?.Invoke();
		}
		catch (Exception value)
		{
			Log($"RecordingControlChanged subscriber failed: {value}", "RaiseRecordingControlChanged", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\SessionCoordinator.cs", 800);
		}
	}

	public async Task StopSessionAsync(string endReason = "user_stop")
	{
		if (endReason == "app_quit")
		{
			_appShutdownRequested = true;
			CancelPendingResumeSettle();
		}
		if (Interlocked.CompareExchange(ref _stopInProgress, 1, 0) != 0)
		{
			Log("StopSession coalesced with active stop active_end_reason=" + (_activeStopReason ?? "-") + " requested_end_reason=" + endReason, "StopSessionAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\SessionCoordinator.cs", 823);
			while (Volatile.Read(in _stopInProgress) != 0)
			{
				await Task.Delay(25).ConfigureAwait(continueOnCapturedContext: false);
			}
			return;
		}
		_activeStopReason = endReason;
		try
		{
			_recoveryGate.CancelPendingRetry();
			CancelPendingResumeSettle();
			_recoveryGate.CloseUnavailabilityInterval("abandoned");
			_recoveryGate.StopSettleTimer();
			SessionState state = State;
			if ((state == SessionState.Idle || state == SessionState.UpdateRequired) ? true : false)
			{
				Log($"StopSession skipped: state={State} endReason={endReason}", "StopSessionAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\SessionCoordinator.cs", 849);
				return;
			}
			_recoveryGate.ResetFailureStreak();
			Log($"StopSession begin endReason={endReason} state={State}", "StopSessionAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\SessionCoordinator.cs", 855);
			RecordSessionEnded(TelemetryMap.SessionEndReasonFor(endReason), cleanStop: true);
			CancelPendingPrivacyResume();
			_stoppedForLowDisk = false;
			StopTimers();
			DetachedRuntime runtime = DetachActiveRuntime();
			await Task.Run((Action)DisposeRuntimeMonitors).ConfigureAwait(continueOnCapturedContext: false);
			try
			{
				string finalEndReason = ((endReason == "user_stop" && runtime.PrivacyContext != null) ? "privacy_mask" : endReason);
				await StopDetachedCaptureAsync(runtime.Engine, "StopSession endReason=" + endReason).ConfigureAwait(continueOnCapturedContext: false);
				await SealDetachedSegmentAsync(runtime.Segment, "StopSession", finalEndReason, (!(finalEndReason == "privacy_mask")) ? null : runtime.PrivacyContext?.Type, (!(finalEndReason == "privacy_mask")) ? null : runtime.PrivacyContext?.Detail).ConfigureAwait(continueOnCapturedContext: false);
				CompleteEventWriter(runtime.Router);
				await Task.Run(() =>
				{
					DisposeDetachedAudio(runtime.SystemAudio, "StopSession endReason=" + endReason);
				}).ConfigureAwait(continueOnCapturedContext: false);
			}
			finally
			{
				ClearActiveClipMarker(runtime.SessionDir);
			}
			SetState((Volatile.Read(in _updateRequiredRequested) != 0) ? SessionState.UpdateRequired : SessionState.Idle);
			runtime.UploadManager?.TriggerUpload();
		}
		finally
		{
			_activeStopReason = null;
			Interlocked.Exchange(ref _stopInProgress, 0);
			string text = endReason;
			if ((text == "app_quit" || text == "force_update") ? true : false)
			{
				Interlocked.Exchange(ref _pendingStartAfterCleanup, 0);
			}
			else
			{
				TryStartPendingSession();
			}
		}
	}

	public async Task EnterUpdateRequiredStateAsync()
	{
		if (State == SessionState.UpdateRequired)
		{
			Log("EnterUpdateRequiredState skipped: already update-required", "EnterUpdateRequiredStateAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\SessionCoordinator.cs", 916);
			return;
		}
		Interlocked.Exchange(ref _updateRequiredRequested, 1);
		_recoveryGate.CancelPendingRetry();
		CancelPendingResumeSettle();
		Log("EnterUpdateRequiredState requested", "EnterUpdateRequiredStateAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\SessionCoordinator.cs", 926);
		await WaitForSessionTransitionsAsync().ConfigureAwait(continueOnCapturedContext: false);
		if (State != SessionState.Idle && State != SessionState.UpdateRequired)
		{
			await StopSessionAsync("force_update").ConfigureAwait(continueOnCapturedContext: false);
		}
		if (Volatile.Read(in _updateRequiredRequested) != 0)
		{
			SetState(SessionState.UpdateRequired);
		}
	}

	public void ExitUpdateRequiredState()
	{
		if (Interlocked.Exchange(ref _updateRequiredRequested, 0) != 0)
		{
			Log($"ExitUpdateRequiredState: the version floor no longer applies state={State}", "ExitUpdateRequiredState", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\SessionCoordinator.cs", 958);
			if (State == SessionState.UpdateRequired)
			{
				SetState(SessionState.Idle);
			}
			RaiseRecordingControlChanged();
			RequestStartAfterUpdateHold();
		}
	}

	public async Task<bool> StopForUpdateInstallAsync(bool immediate, CancellationToken ct)
	{
		if (_appShutdownRequested || ct.IsCancellationRequested)
		{
			return false;
		}
		TaskCompletionSource<bool> done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
		if (immediate || (State != SessionState.Recording && !PausedForSleepOrLogoff))
		{
			Interlocked.Exchange(ref _updateBoundary, null)?.TrySetResult(result: false);
			BeginUpdateInstallStop(done, immediate ? "immediate" : "no_open_segment");
			return await done.Task.ConfigureAwait(continueOnCapturedContext: false);
		}
		Interlocked.Exchange(ref _updateBoundary, done)?.TrySetResult(result: false);
		if (State == SessionState.Recording)
		{
			Log($"update install: waiting for the next segment boundary segment={_currentSegmentIndex}", "StopForUpdateInstallAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\SessionCoordinator.cs", 997);
		}
		else
		{
			Log($"update install: waiting for the resume paused_for={_pausedFor ?? "-"} suspend_fence={_availabilityEvents.SuspendFenceArmed} suspend_latched={_availabilityEvents.SuspendLatched}", "StopForUpdateInstallAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\SessionCoordinator.cs", 1001);
		}
		if (State != SessionState.Recording && !PausedForSleepOrLogoff)
		{
			TryTakeUpdateBoundary("no_open_segment");
		}
		using (ct.Register(() =>
		{
			if (Interlocked.CompareExchange(ref _updateBoundary, null, done) == done)
			{
				done.TrySetResult(result: false);
			}
		}))
		{
			return await done.Task.ConfigureAwait(continueOnCapturedContext: false);
		}
	}

	public void ReleaseUpdateInstallHold()
	{
		if (Interlocked.Exchange(ref _updateInstallHold, 0) != 0)
		{
			Log($"update install did not happen; the recording hold lifts state={State}", "ReleaseUpdateInstallHold", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\SessionCoordinator.cs", 1031);
			RaiseRecordingControlChanged();
			if (State == SessionState.Paused && _pausedFor == "force_update")
			{
				_pausedFor = null;
				OnResume();
			}
			else
			{
				RequestStartAfterUpdateHold();
			}
		}
	}

	private void RequestStartAfterUpdateHold()
	{
		if (!UpdateHoldsRecording && !_appShutdownRequested && State == SessionState.Idle && _auth.IsSignedIn)
		{
			_recoveryGate.RequestStart(RecoveryStartInitiator.SyncTick);
		}
	}

	private bool TryTakeUpdateBoundary(string at)
	{
		if (Volatile.Read(in _updateBoundary) == null)
		{
			return false;
		}
		TaskCompletionSource<bool> taskCompletionSource = Interlocked.Exchange(ref _updateBoundary, null);
		if (taskCompletionSource == null)
		{
			return false;
		}
		BeginUpdateInstallStop(taskCompletionSource, at);
		return true;
	}

	private void BeginUpdateInstallStop(TaskCompletionSource<bool> done, string at)
	{
		Interlocked.Exchange(ref _updateInstallHold, 1);
		Task.Run(async () =>
		{
			bool ended = false;
			try
			{
				_recoveryGate.CancelPendingRetry();
				CancelPendingResumeSettle();
				await EndSessionForUpdateInstallAsync(at).ConfigureAwait(continueOnCapturedContext: false);
				SessionState state = State;
				bool flag = (uint)(state - 1) <= 2u;
				ended = !flag;
				if (!ended)
				{
					Log($"update install: the session still runs after the stop at={at} state={State}", "BeginUpdateInstallStop", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\SessionCoordinator.cs", 1090);
				}
			}
			catch (Exception ex)
			{
				Log("update install: ending the session failed at=" + at + ": " + KuroLog.ExceptionSummary(ex), "BeginUpdateInstallStop", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\SessionCoordinator.cs", 1095);
			}
			finally
			{
				if (!ended)
				{
					ReleaseUpdateInstallHold();
					ResumeAfterFailedBoundaryStop(at);
				}
				done.TrySetResult(ended);
			}
		});
	}

	private void ResumeAfterFailedBoundaryStop(string at)
	{
		if (!_appShutdownRequested)
		{
			if (at == "segment_boundary" && State == SessionState.Recording)
			{
				Log($"update install: the stop at the segment boundary failed; the rotation timer is re-armed segment={_currentSegmentIndex}", "ResumeAfterFailedBoundaryStop", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\SessionCoordinator.cs", 1126);
				RestartClipRotationTimer(1000.0);
			}
			else if (at == "resume" && State == SessionState.Paused)
			{
				Log("update install: the stop at the resume failed; the idle latch is re-armed", "ResumeAfterFailedBoundaryStop", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\SessionCoordinator.cs", 1131);
				_idleManager?.RearmIdleForRetry();
			}
		}
	}

	private async Task EndSessionForUpdateInstallAsync(string at)
	{
		await WaitForSessionTransitionsAsync().ConfigureAwait(continueOnCapturedContext: false);
		SessionState state = State;
		Log($"update install: recording ends at={at} state={state} segment={_currentSegmentIndex}", "EndSessionForUpdateInstallAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\SessionCoordinator.cs", 1144);
		if ((uint)(state - 1) <= 2u)
		{
			await StopSessionAsync("force_update").ConfigureAwait(continueOnCapturedContext: false);
		}
		Task[] pending = _finalizesInFlight.Keys.ToArray();
		if (pending.Length != 0)
		{
			Task all = Task.WhenAll(pending);
			if (await Task.WhenAny(all, Task.Delay(UpdateInstallSealWait)).ConfigureAwait(continueOnCapturedContext: false) != all)
			{
				Log($"update install: {pending.Length} segment seal(s) still running after {(int)UpdateInstallSealWait.TotalSeconds}s", "EndSessionForUpdateInstallAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\SessionCoordinator.cs", 1157);
			}
		}
	}

	private async Task WaitForSessionTransitionsAsync()
	{
		while (Volatile.Read(in _stopInProgress) != 0 || Volatile.Read(in _startInProgress) != 0 || Volatile.Read(in _captureFailureHandling) != 0)
		{
			await Task.Delay(25).ConfigureAwait(continueOnCapturedContext: false);
		}
	}

	private void StartCaptureEngine(DisplayTopologyPolicyResult displayPolicy)
	{
		if (CurrentSessionDir == null || _frameState == null || _writeRouter == null)
		{
			throw new InvalidOperationException("Cannot start capture engine without an active session.");
		}
		SessionCaptureEngine sessionCaptureEngine = new SessionCaptureEngine(CurrentSessionDir, _frameState, _writeRouter, displayPolicy)
		{
			CaptureFailed = OnRecorderCaptureFailed,
			CaptureAccessDenied = OnRecorderCaptureAccessDenied,
			DisplayPolicyLine = DisplayPolicySelectedLine(displayPolicy),
			LegacyPadCadence = () => CurrentFeatureFlags().LegacyPadCadence,
			SystemAudio = _systemAudio
		};
		try
		{
			sessionCaptureEngine.StartCapture();
			_captureEngine = sessionCaptureEngine;
		}
		catch
		{
			try
			{
				sessionCaptureEngine.DisposeAsync().AsTask().GetAwaiter()
					.GetResult();
			}
			catch (Exception ex)
			{
				Log("Capture engine dispose after start failure also failed: " + ex.Message, "StartCaptureEngine", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\SessionCoordinator.cs", 1218);
			}
			throw;
		}
	}

	private DisplayTopologyPolicyResult EvaluateDisplayPolicyForStart()
	{
		DisplayTopologyPolicyResult displayTopologyPolicyResult = DisplayTopologyPolicy.Evaluate(DisplayTopologyPolicy.ProfileForIndex(0));
		if (!displayTopologyPolicyResult.IsSupported)
		{
			return displayTopologyPolicyResult;
		}
		AdaptiveCaptureProfileSelection selection = _adaptiveProfileStore.GetSelection(displayTopologyPolicyResult.TopologySignature);
		DisplayTopologyPolicyResult displayTopologyPolicyResult2 = DisplayTopologyPolicy.Evaluate(selection.Profile);
		if (displayTopologyPolicyResult2.IsSupported && displayTopologyPolicyResult2.TopologySignature != displayTopologyPolicyResult.TopologySignature)
		{
			selection = _adaptiveProfileStore.GetSelection(displayTopologyPolicyResult2.TopologySignature);
			displayTopologyPolicyResult2 = DisplayTopologyPolicy.Evaluate(selection.Profile);
		}
		return displayTopologyPolicyResult2 with
		{
			AdaptiveProfileReason = selection.Reason,
			AdaptiveProfileFailureCount = selection.ConsecutiveFailures
		};
	}

	private static string DisplayPolicySelectedLine(DisplayTopologyPolicyResult selected)
	{
		return $"Display policy selected mode={selected.Mode} epoch={selected.TopologyEpoch} physical={selected.PhysicalDisplayCount} logical={selected.LogicalDisplayCount} profile={selected.Profile.Name} profile_reason={selected.AdaptiveProfileReason} profile_failures={selected.AdaptiveProfileFailureCount} unsupported={selected.UnsupportedReason ?? "-"} " + DisplayPolicyDebug(selected);
	}

	private static string DisplayPolicyDebug(DisplayTopologyPolicyResult policy)
	{
		string text = ((policy.Displays.Count == 0) ? "-" : string.Join(";", policy.Displays.Select((CaptureDisplaySettings display) => $"d{display.LogicalDisplayIndex}:orig={display.OriginalScreenIndex}:role={display.Role}:native={display.NativeBounds.Width}x{display.NativeBounds.Height}:capture={display.CaptureWidth}x{display.CaptureHeight}:fps={display.CaptureFps}:mode={display.ResolutionMode}")));
		return "signature=\"" + policy.TopologySignature + "\" displays=" + text;
	}

	private void RecordAdaptiveCaptureFailure(Exception ex)
	{
		DisplayTopologyPolicyResult activeDisplayPolicy = _activeDisplayPolicy;
		if ((object)activeDisplayPolicy == null || !activeDisplayPolicy.IsSupported)
		{
			return;
		}
		try
		{
			AdaptiveCaptureProfile adaptiveCaptureProfile = _adaptiveProfileStore.RecordFailure(activeDisplayPolicy, ex.GetType().Name + ":" + ex.Message);
			if (adaptiveCaptureProfile.Index != activeDisplayPolicy.Profile.Index)
			{
				KuroLog.Capture($"Adaptive capture profile will use {adaptiveCaptureProfile.Name} on next start previous={activeDisplayPolicy.Profile.Name}", "RecordAdaptiveCaptureFailure", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\SessionCoordinator.cs", 1283);
			}
		}
		catch (Exception ex2)
		{
			KuroLog.Capture("Adaptive capture failure record failed: " + ex2.Message, "RecordAdaptiveCaptureFailure", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\SessionCoordinator.cs", 1290);
		}
	}

	private async Task HandleTopologyChangeAsync()
	{
		if (Interlocked.Exchange(ref _captureFailureHandling, 1) != 0)
		{
			Log("Topology change ignored because recovery is active", "HandleTopologyChangeAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\SessionCoordinator.cs", 1298);
			return;
		}
		bool recheckPrivacyResume = false;
		Exception failureEx = null;
		try
		{
			bool flag = Volatile.Read(in _stopInProgress) != 0;
			if (!flag)
			{
				SessionState state = State;
				bool flag2 = (uint)(state - 1) <= 2u;
				flag = !flag2;
			}
			if (flag)
			{
				return;
			}
			DisplayTopologyPolicyResult newPolicy = EvaluateDisplayPolicyForStart();
			if (!newPolicy.IsSupported)
			{
				_unsupportedTopologyReason = newPolicy.UnsupportedReason ?? "unsupported_display_topology";
				_lastUnsupportedLogSignature = newPolicy.TopologySignature;
				Log("Topology change produced unsupported topology reason=" + newPolicy.UnsupportedReason, "HandleTopologyChangeAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\SessionCoordinator.cs", 1320);
				await StopSessionAsync(newPolicy.UnsupportedReason ?? "unsupported_display_topology").ConfigureAwait(continueOnCapturedContext: false);
				return;
			}
			DisplayTopologyPolicyResult activeDisplayPolicy = _activeDisplayPolicy;
			if ((object)activeDisplayPolicy != null && _captureEngine != null && string.Equals(activeDisplayPolicy.TopologySignature, newPolicy.TopologySignature, StringComparison.Ordinal))
			{
				return;
			}
			Log("Display topology changed; restarting capture at segment boundary", "HandleTopologyChangeAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\SessionCoordinator.cs", 1337);
			_recoveryGate.StopSettleTimer();
			CancelPendingPrivacyResume();
			StopTimers();
			SegmentSet segment = null;
			lock (_writeRoutingSync)
			{
				segment = _writeRouter?.Stop();
				_activeSegment = null;
			}
			SessionCaptureEngine engine = Interlocked.Exchange(ref _captureEngine, null);
			await StopDetachedCaptureAsync(engine, "Topology change").ConfigureAwait(continueOnCapturedContext: false);
			ClearActiveClipMarker();
			if (segment != null)
			{
				try
				{
					await segment.SealAsync("topology_change", null, null, "windows_display_settings").ConfigureAwait(continueOnCapturedContext: false);
				}
				finally
				{
					_writeRouter?.ClearPrevious(segment);
				}
				_uploadManager?.TriggerUpload();
			}
			_activeDisplayPolicy = newPolicy;
			if (Volatile.Read(in _stopInProgress) != 0)
			{
				Log("Topology change superseded by stop before capture restart", "HandleTopologyChangeAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\SessionCoordinator.cs", 1374);
				return;
			}
			StartCaptureEngine(newPolicy);
			bool flag3;
			lock (_writeRoutingSync)
			{
				flag3 = _writeRouter == null || Volatile.Read(in _stopInProgress) != 0;
			}
			if (flag3)
			{
				SessionCaptureEngine engine2 = Interlocked.Exchange(ref _captureEngine, null);
				Log("Topology change superseded by concurrent stop; reaping freshly-started engine", "HandleTopologyChangeAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\SessionCoordinator.cs", 1396);
				await StopDetachedCaptureAsync(engine2, "Topology change superseded by stop").ConfigureAwait(continueOnCapturedContext: false);
				return;
			}
			EnsureDiskCheckTimer();
			if (State == SessionState.Recording && (_availabilityEvents.SuspendFenceArmed || _availabilityEvents.SuspendLatched))
			{
				lock (_writeRoutingSync)
				{
					_pausedFor = "system_sleep";
					State = SessionState.Paused;
				}
				ClearActiveClipMarker();
				NotifyStateChanged(SessionState.Paused);
				Log("Topology recovery parked in Paused: suspend fence/latch armed", "HandleTopologyChangeAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\SessionCoordinator.cs", 1421);
			}
			else if (State == SessionState.Recording)
			{
				lock (_writeRoutingSync)
				{
					OpenSegmentLocked(_currentSegmentIndex + 1);
				}
				_systemMonitor?.LogInitialState();
				RestartClipRotationTimer();
				MarkCurrentClipActive();
			}
			else
			{
				ClearActiveClipMarker();
				recheckPrivacyResume = State == SessionState.PrivacyPaused;
			}
			_recoveryGate.ArmSettleTimer();
			Log($"Topology change recovery complete mode={newPolicy.Mode} epoch={newPolicy.TopologyEpoch} profile={newPolicy.Profile.Name} displays={newPolicy.LogicalDisplayCount} " + DisplayPolicyDebug(newPolicy), "HandleTopologyChangeAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\SessionCoordinator.cs", 1440);
		}
		catch (Exception ex)
		{
			Log("Topology change recovery failed: " + KuroLog.ExceptionSummary(ex), "HandleTopologyChangeAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\SessionCoordinator.cs", 1452);
			failureEx = ex;
		}
		finally
		{
			Interlocked.Exchange(ref _captureFailureHandling, 0);
			if (failureEx != null)
			{
				HandleCaptureFailureAsync(failureEx);
			}
			else
			{
				if (recheckPrivacyResume)
				{
					RecheckParkedPrivacyContext();
				}
				TryStartPendingSession();
			}
		}
	}

	private void ArmLockShellRecheckIfNeeded(PrivacyContext context)
	{
		if (context.Type != "os_lock_screen" || Volatile.Read(in _lockShellRecheckTimer) != null)
		{
			return;
		}
		_parkedRecheckKeptLogged = false;
		System.Timers.Timer timer = new System.Timers.Timer(5000.0)
		{
			AutoReset = true
		};
		timer.Elapsed += (object? _, ElapsedEventArgs _) =>
		{
			try
			{
				if (Volatile.Read(in _lockShellRecheckTimer) != timer || State != SessionState.PrivacyPaused || _activePrivacyContext?.Type != "os_lock_screen")
				{
					Interlocked.CompareExchange(ref _lockShellRecheckTimer, null, timer);
					timer.Stop();
					timer.Dispose();
				}
				else
				{
					RecheckParkedPrivacyContext();
				}
			}
			catch (Exception ex)
			{
				Log("lock shell recheck failed: " + ex.Message, "ArmLockShellRecheckIfNeeded", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\SessionCoordinator.cs", 1521);
			}
		};
		System.Timers.Timer timer2 = Interlocked.Exchange(ref _lockShellRecheckTimer, timer);
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

	private void RecheckParkedPrivacyContext()
	{
		if (State != SessionState.PrivacyPaused || Volatile.Read(in _stopInProgress) != 0 || Volatile.Read(in _captureFailureHandling) != 0)
		{
			return;
		}
		PrivacyContext activePrivacyContext = _activePrivacyContext;
		if (activePrivacyContext == null)
		{
			return;
		}
		try
		{
			ForegroundWindowInfo foregroundWindowInfo = _inputMonitor?.GetCurrentForegroundInfo();
			if (foregroundWindowInfo == null)
			{
				return;
			}
			bool isSecureFieldFocused = _inputMonitor?.GetCurrentSecureFieldState() ?? false;
			bool isCameraActive = _cameraMonitor?.IsActive ?? false;
			PrivacyContext privacyContext = _privacyManager?.Prime(foregroundWindowInfo, isSecureFieldFocused, isCameraActive, CaptureContended);
			if (privacyContext != null)
			{
				_activePrivacyContext = privacyContext;
				ArmLockShellRecheckIfNeeded(privacyContext);
				if (!_parkedRecheckKeptLogged)
				{
					_parkedRecheckKeptLogged = true;
					Log($"Parked privacy recheck kept privacy pause trigger={privacyContext.Type} detail={privacyContext.Detail ?? "-"}", "RecheckParkedPrivacyContext", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\SessionCoordinator.cs", 1575);
				}
			}
			else
			{
				_parkedRecheckKeptLogged = false;
				Log("Parked privacy recheck found privacy clear; scheduling resume", "RecheckParkedPrivacyContext", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\SessionCoordinator.cs", 1583);
				HandlePrivacyEndAsync(activePrivacyContext);
			}
		}
		catch (Exception value)
		{
			Log($"Parked privacy recheck failed: {value}", "RecheckParkedPrivacyContext", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\SessionCoordinator.cs", 1588);
		}
	}

	private void OpenSegment()
	{
		lock (_writeRoutingSync)
		{
			OpenSegmentLocked(_currentSegmentIndex);
		}
	}

	private void OpenSegmentLocked(int segmentIndex)
	{
		SessionCaptureEngine captureEngine = _captureEngine;
		RecorderWriteRouter writeRouter = _writeRouter;
		if (CurrentSessionDir == null || captureEngine == null || writeRouter == null)
		{
			throw new InvalidOperationException("Cannot open a segment before capture is running.");
		}
		long clockCorrectionMs = RecordingClock.Shared.Relatch("segment_open");
		SegmentSet segmentSet = captureEngine.CreateSegment(segmentIndex, SegmentOpenReason.Fresh, clockCorrectionMs);
		writeRouter.Install(segmentSet);
		_currentSegmentIndex = segmentIndex;
		Telemetry.SetContext(Path.GetFileName(CurrentSessionDir), segmentIndex);
		_activeSegment = segmentSet;
		MarkCurrentClipActive();
	}

	private async void RotateClipSeamlessly(int timerGeneration)
	{
		if (timerGeneration != Volatile.Read(in _clipRotationTimerGeneration))
		{
			return;
		}
		if (Interlocked.Exchange(ref _rotationInProgress, 1) != 0)
		{
			Log("RotateClipSeamlessly skipped because a rotation is already in progress", "RotateClipSeamlessly", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\SessionCoordinator.cs", 1630);
			return;
		}
		SegmentSet oldToFinalize = null;
		SegmentSet segmentSet = null;
		bool flag = false;
		TaskCompletionSource<bool> taskCompletionSource = null;
		try
		{
			int num = 0;
			object obj = default;
			try
			{
				lock (_writeRoutingSync)
				{
					SessionCaptureEngine captureEngine = _captureEngine;
					RecorderWriteRouter writeRouter = _writeRouter;
					if (timerGeneration != Volatile.Read(in _clipRotationTimerGeneration) || State != SessionState.Recording || Volatile.Read(in _stopInProgress) != 0 || Volatile.Read(in _captureFailureHandling) != 0 || CurrentSessionDir == null || captureEngine == null || writeRouter == null)
					{
						return;
					}
					if (_availabilityEvents.SuspendFenceArmed)
					{
						Log("RotateClipSeamlessly skipped: suspend fence armed; pending suspend seal owns the segment", "RotateClipSeamlessly", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\SessionCoordinator.cs", 1662);
						return;
					}
					taskCompletionSource = Interlocked.Exchange(ref _updateBoundary, null);
					if (taskCompletionSource == null)
					{
						int num2 = _currentSegmentIndex + 1;
						segmentSet = captureEngine.CreateSegment(num2, SegmentOpenReason.Rotation, 0L);
						oldToFinalize = writeRouter.SwapForRotation(segmentSet, (long)RecordingClock.Shared.NowMs());
						_currentSegmentIndex = num2;
						Telemetry.SetContext(Path.GetFileName(CurrentSessionDir), num2);
						_activeSegment = segmentSet;
						flag = true;
						MarkCurrentClipActive();
					}
				}
				if (taskCompletionSource != null)
				{
					BeginUpdateInstallStop(taskCompletionSource, "segment_boundary");
					return;
				}
				_systemMonitor?.LogInitialState();
				RestartClipRotationTimer();
				if (oldToFinalize != null)
				{
					FinalizeStoppedSegmentAsync(oldToFinalize, "timer_rotation");
				}
			}
			catch (Exception ex)
			{
				obj = ex;
				num = 1;
			}
			if (num != 1)
			{
				return;
			}
			Exception ex2 = (Exception)obj;
			if (!flag && segmentSet != null)
			{
				try
				{
					await segmentSet.SealAsync("capture_failure", null, null, "segment_timer", ex2).ConfigureAwait(continueOnCapturedContext: false);
				}
				catch (Exception ex3)
				{
					Log("RotateClipSeamlessly orphan segment finalize failed: " + KuroLog.ExceptionSummary(ex3), "RotateClipSeamlessly", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\SessionCoordinator.cs", 1701);
				}
			}
			if (oldToFinalize != null)
			{
				try
				{
					await oldToFinalize.SealAsync("timer_rotation").ConfigureAwait(continueOnCapturedContext: false);
				}
				catch (Exception ex4)
				{
					Log("RotateClipSeamlessly fallback finalize failed: " + KuroLog.ExceptionSummary(ex4), "RotateClipSeamlessly", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\SessionCoordinator.cs", 1706);
				}
				_uploadManager?.TriggerUpload();
			}
			Log("RotateClipSeamlessly failed: " + KuroLog.ExceptionSummary(ex2), "RotateClipSeamlessly", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\SessionCoordinator.cs", 1709);
			HandleCaptureFailureAsync(ex2);
		}
		finally
		{
			Interlocked.Exchange(ref _rotationInProgress, 0);
		}
	}

	private void OnIdle(string reason)
	{
		if (Volatile.Read(in _stopInProgress) != 0 || Volatile.Read(in _captureFailureHandling) != 0)
		{
			return;
		}
		long num = (long)RecordingClock.Shared.NowMs();
		long? endedWallMs = null;
		if (reason == "system_sleep")
		{
			long num2 = (_availabilityEvents.SuspendFenceArmed ? _availabilityEvents.SuspendFenceWallMs : _availabilityEvents.LastSuspendAtWallMs);
			if (num2 > 0 && num2 != long.MaxValue)
			{
				num = Math.Min(num, num2);
			}
			endedWallMs = num;
		}
		bool flag = false;
		StopClipRotationTimer();
		SegmentSet segmentSet;
		lock (_writeRoutingSync)
		{
			if (State != SessionState.Recording || Volatile.Read(in _stopInProgress) != 0 || Volatile.Read(in _captureFailureHandling) != 0)
			{
				return;
			}
			long num3 = _writeRouter?.EpisodeCaptureFloorWallMs ?? 0;
			if (num3 > 0 && num < num3)
			{
				num = num3;
				if (endedWallMs.HasValue)
				{
					endedWallMs = num3;
				}
			}
			segmentSet = _writeRouter?.StopForIdle(num);
			_activeSegment = null;
			_pausedFor = reason;
			State = SessionState.Paused;
			flag = true;
		}
		if (flag)
		{
			NotifyStateChanged(SessionState.Paused);
		}
		if (segmentSet != null)
		{
			try
			{
				Task existingSealTask = segmentSet.SealAsync(reason, null, null, null, null, writeSegmentEnd: true, endedWallMs);
				ClearActiveClipMarker();
				FinalizeStoppedSegmentAsync(segmentSet, reason, existingSealTask);
			}
			catch (Exception ex)
			{
				Log($"OnIdle finalize failed: {ex}", "OnIdle", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\SessionCoordinator.cs", 1794);
				HandleCaptureFailureAsync(ex);
				return;
			}
		}
		if ((!(reason == "system_sleep") && !(reason == "session_logoff")) || 1 == 0)
		{
			TryTakeUpdateBoundary(reason);
		}
	}

	private void OnResume()
	{
		if (State != SessionState.Paused || Volatile.Read(in _stopInProgress) != 0 || Volatile.Read(in _captureFailureHandling) != 0 || TryTakeUpdateBoundary("resume"))
		{
			return;
		}
		if (!ShouldRecordNow())
		{
			StopSessionAsync(StopReasonForCurrentRecordingGate());
			return;
		}
		if (!SystemAvailabilityEvents.QueryWtsSessionInteractive())
		{
			_parks.Refused(ResumeRefusal.SessionNotInteractive);
			_idleManager?.RearmIdleForRetry();
			_writeRouter?.DiscardPreOpenHold();
			if (!_resumeInteractiveDeferLogged)
			{
				_resumeInteractiveDeferLogged = true;
				KuroLog.Capture($"os_recovery event=resume_deferred reason=session_not_interactive state={State}", "OnResume", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\SessionCoordinator.cs", 1852);
			}
			return;
		}
		ForegroundWindowInfo foregroundWindowInfo = _inputMonitor?.GetCurrentForegroundInfo();
		bool captureContended = CaptureContended;
		if ((foregroundWindowInfo != null) | captureContended)
		{
			bool isSecureFieldFocused = _inputMonitor?.GetCurrentSecureFieldState() ?? false;
			bool isCameraActive = _cameraMonitor?.IsActive ?? false;
			PrivacyContext privacyContext = ((!(foregroundWindowInfo != null)) ? _privacyManager?.PrimeCaptureContention(captureContended) : _privacyManager?.Prime(foregroundWindowInfo, isSecureFieldFocused, isCameraActive, captureContended));
			if (privacyContext != null)
			{
				_activePrivacyContext = privacyContext;
				if (_inputMonitor != null)
				{
					_inputMonitor.SuppressKeys = true;
				}
				Log("OnResume blocked by privacy context trigger=" + privacyContext.Type + " detail=" + (privacyContext.Detail ?? "-"), "OnResume", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\SessionCoordinator.cs", 1870);
				_writeRouter?.NotePrivacyGap();
				SetState(SessionState.PrivacyPaused);
				ArmLockShellRecheckIfNeeded(privacyContext);
				return;
			}
		}
		else
		{
			PrivacyManager? privacyManager = _privacyManager;
			if (privacyManager != null && privacyManager.LastKnownExeIsLockShell)
			{
				_parks.Refused(ResumeRefusal.LockShellUnresolved);
				_idleManager?.RearmIdleForRetry();
				_writeRouter?.DiscardPreOpenHold();
				if (!_resumeInteractiveDeferLogged)
				{
					_resumeInteractiveDeferLogged = true;
					Log("OnResume held: foreground unresolved while lock shell last known", "OnResume", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\SessionCoordinator.cs", 1891);
				}
				return;
			}
		}
		bool flag = false;
		Exception ex = null;
		try
		{
			lock (_writeRoutingSync)
			{
				if (State != SessionState.Paused || Volatile.Read(in _stopInProgress) != 0 || Volatile.Read(in _captureFailureHandling) != 0)
				{
					return;
				}
				if (_availabilityEvents.SuspendFenceArmed)
				{
					_idleManager?.RearmIdleForRetry();
					_parks.Refused(ResumeRefusal.SuspendFence);
					return;
				}
				OpenSegmentLocked(_currentSegmentIndex + 1);
				State = SessionState.Recording;
				flag = true;
			}
		}
		catch (Exception ex2)
		{
			ex = ex2;
		}
		if (flag)
		{
			_resumeInteractiveDeferLogged = false;
			Log($"session resumed segment={_currentSegmentIndex}", "OnResume", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\SessionCoordinator.cs", 1938);
		}
		if (!flag)
		{
			if (ex != null)
			{
				Log($"OnResume failed to start clip: {ex}", "OnResume", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\SessionCoordinator.cs", 1945);
				HandleCaptureFailureAsync(ex);
			}
		}
		else
		{
			RestartClipRotationTimer();
			_systemMonitor?.LogInitialState();
			NotifyStateChanged(SessionState.Recording);
		}
	}

	private void OnPrivacyStart(PrivacyContext context)
	{
		HandlePrivacyStartAsync(context);
	}

	private async Task HandlePrivacyStartAsync(PrivacyContext context)
	{
		bool gateHeld = false;
		try
		{
			if (Volatile.Read(in _stopInProgress) != 0 || Volatile.Read(in _captureFailureHandling) != 0)
			{
				return;
			}
			CancelPendingPrivacyResume();
			await _privacyTransitionGate.WaitAsync().ConfigureAwait(continueOnCapturedContext: false);
			gateHeld = true;
			Log($"Privacy start trigger={context.Type} detail={context.Detail ?? "-"} masked_display={context.DisplayIndex} title={context.WindowTitle}", "HandlePrivacyStartAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\SessionCoordinator.cs", 1981);
			Count.MaskTriggers(1L);
			_ = Task.CompletedTask;
			SegmentSet segmentSet = null;
			bool flag = false;
			bool flag2 = false;
			lock (_writeRoutingSync)
			{
				if (Volatile.Read(in _stopInProgress) != 0 || Volatile.Read(in _captureFailureHandling) != 0)
				{
					return;
				}
				long num = (long)RecordingClock.Shared.NowMs();
				SessionState state = State;
				if ((uint)(state - 1) <= 2u)
				{
					_activePrivacyContext = context;
					long num2 = Math.Max(0L, num - 1);
					LogRow(new
					{
						type = "privacy_mask_start",
						timestamp = num2,
						trigger = context.Type,
						maskTrigger = context.Type,
						maskDetail = context.Detail,
						appName = context.AppName,
						exeName = context.ExeName,
						windowTitle = context.WindowTitle,
						maskedDisplay = context.DisplayIndex
					}, num2);
					segmentSet = _writeRouter?.StopForPrivacy(num);
					_activeSegment = null;
					if (State != SessionState.PrivacyPaused)
					{
						State = SessionState.PrivacyPaused;
						flag2 = true;
					}
					flag = true;
				}
			}
			if (flag)
			{
				if (_inputMonitor != null)
				{
					_inputMonitor.SuppressKeys = true;
				}
				StopClipRotationTimer();
				ArmLockShellRecheckIfNeeded(context);
				if (flag2)
				{
					NotifyStateChanged(SessionState.PrivacyPaused);
				}
				if (segmentSet != null)
				{
					Task existingSealTask = segmentSet.SealAsync("privacy_mask", context.Type, context.Detail);
					FinalizeStoppedSegmentAsync(segmentSet, "privacy_mask", existingSealTask);
				}
				ClearActiveClipMarker();
				Log("Privacy start: write gate closed; capture remains hot and output finalization continues in background", "HandlePrivacyStartAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\SessionCoordinator.cs", 2063);
				TryTakeUpdateBoundary("privacy_mask");
			}
		}
		catch (Exception ex)
		{
			Log($"OnPrivacyStart failed: {ex}", "HandlePrivacyStartAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\SessionCoordinator.cs", 2068);
			HandleCaptureFailureAsync(ex);
		}
		finally
		{
			if (gateHeld)
			{
				_privacyTransitionGate.Release();
			}
		}
	}

	private void OnPrivacyEnd(PrivacyContext context)
	{
		HandlePrivacyEndAsync(context);
	}

	private async Task HandlePrivacyEndAsync(PrivacyContext context)
	{
		if (State != SessionState.PrivacyPaused || Volatile.Read(in _stopInProgress) != 0 || Volatile.Read(in _captureFailureHandling) != 0)
		{
			return;
		}
		CancellationToken resumeToken = ReplacePrivacyResumeCts();
		try
		{
			Log($"Privacy end scheduled trigger={context.Type} detail={context.Detail ?? "-"} masked_display={context.DisplayIndex} title={context.WindowTitle}", "HandlePrivacyEndAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\SessionCoordinator.cs", 2101);
			await Task.Delay(CurrentMaskingPolicy().PrivacyResumeLingerMs, resumeToken).ConfigureAwait(continueOnCapturedContext: false);
		}
		catch (OperationCanceledException)
		{
			Log("Privacy end canceled before resume delay elapsed", "HandlePrivacyEndAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\SessionCoordinator.cs", 2106);
			return;
		}
		try
		{
			if (resumeToken.IsCancellationRequested || State != SessionState.PrivacyPaused || Volatile.Read(in _stopInProgress) != 0 || Volatile.Read(in _captureFailureHandling) != 0)
			{
				return;
			}
			if (CurrentSessionDir == null)
			{
				_activePrivacyContext = null;
				if (_inputMonitor != null)
				{
					_inputMonitor.SuppressKeys = false;
				}
				return;
			}
			for (int attempt = 1; attempt <= 3; attempt++)
			{
				bool gateHeld = false;
				if (resumeToken.IsCancellationRequested || State != SessionState.PrivacyPaused || Volatile.Read(in _stopInProgress) != 0 || Volatile.Read(in _captureFailureHandling) != 0)
				{
					break;
				}
				try
				{
					await _privacyTransitionGate.WaitAsync(resumeToken).ConfigureAwait(continueOnCapturedContext: false);
					gateHeld = true;
					ForegroundWindowInfo foregroundWindowInfo = _inputMonitor?.GetCurrentForegroundInfo();
					bool captureContended = CaptureContended;
					if ((foregroundWindowInfo != null) | captureContended)
					{
						bool isSecureFieldFocused = _inputMonitor?.GetCurrentSecureFieldState() ?? false;
						bool isCameraActive = _cameraMonitor?.IsActive ?? false;
						PrivacyContext privacyContext = ((!(foregroundWindowInfo != null)) ? _privacyManager?.PrimeCaptureContention(captureContended) : _privacyManager?.Prime(foregroundWindowInfo, isSecureFieldFocused, isCameraActive, captureContended));
						if (privacyContext != null)
						{
							_parks.Refused(ResumeRefusal.PrivacyStillActive);
							Log("Privacy end: context still sensitive at resume time (trigger=" + privacyContext.Type + "), staying paused", "HandlePrivacyEndAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\SessionCoordinator.cs", 2161);
							ArmLockShellRecheckIfNeeded(privacyContext);
							break;
						}
					}
					else if (_privacyManager?.LastKnownExeIsLockShell ?? false)
					{
						PrivacyContext activePrivacyContext = _activePrivacyContext;
						if ((object)activePrivacyContext != null)
						{
							ArmLockShellRecheckIfNeeded(activePrivacyContext);
						}
						_parks.Refused(ResumeRefusal.LockShellUnresolved);
						Log("Privacy end: foreground unresolved while lock shell last known; staying paused", "HandlePrivacyEndAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\SessionCoordinator.cs", 2181);
						break;
					}
					if (resumeToken.IsCancellationRequested)
					{
						break;
					}
					if (_idleManager?.IsIdle ?? false)
					{
						bool flag = false;
						lock (_writeRoutingSync)
						{
							if (State == SessionState.PrivacyPaused && Volatile.Read(in _stopInProgress) == 0 && Volatile.Read(in _captureFailureHandling) == 0)
							{
								_pausedFor = null;
								State = SessionState.Paused;
								flag = true;
							}
						}
						if (flag)
						{
							_activePrivacyContext = null;
							if (_inputMonitor != null)
							{
								_inputMonitor.SuppressKeys = false;
							}
							_writeRouter?.NoteMaskCleared(holdInput: true);
							_parks.Refused(ResumeRefusal.UserIdle);
							NotifyStateChanged(SessionState.Paused);
							Log("Privacy end: user idle at resume time; parked in Paused until real input", "HandlePrivacyEndAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\SessionCoordinator.cs", 2221);
						}
						break;
					}
					int? num = null;
					bool flag2 = false;
					string text = null;
					ResumeRefusal? resumeRefusal = null;
					lock (_writeRoutingSync)
					{
						if (State != SessionState.PrivacyPaused || Volatile.Read(in _stopInProgress) != 0 || Volatile.Read(in _captureFailureHandling) != 0)
						{
							break;
						}
						if (_availabilityEvents.SuspendFenceArmed)
						{
							_pausedFor = "system_sleep";
							State = SessionState.Paused;
							_idleManager?.RearmIdleForRetry();
							_writeRouter?.NoteMaskCleared(holdInput: true);
							text = "suspend fence armed";
							resumeRefusal = ResumeRefusal.SuspendFence;
						}
						else if (!SystemAvailabilityEvents.QueryWtsSessionInteractive())
						{
							_pausedFor = null;
							State = SessionState.Paused;
							_idleManager?.RearmIdleForRetry();
							_writeRouter?.NoteMaskCleared(holdInput: false);
							text = "session not interactive";
							resumeRefusal = ResumeRefusal.SessionNotInteractive;
						}
						else if (_activeSegment != null || (_writeRouter?.ActiveSegmentIndex.HasValue ?? false))
						{
							SegmentSet? activeSegment = _activeSegment;
							num = ((activeSegment != null) ? new int?(activeSegment.SegmentIndex) : _writeRouter?.ActiveSegmentIndex);
							flag2 = true;
							resumeRefusal = ResumeRefusal.SegmentActive;
						}
						else
						{
							OpenSegmentLocked(_currentSegmentIndex + 1);
							State = SessionState.Recording;
						}
					}
					if (resumeRefusal.HasValue)
					{
						ResumeRefusal valueOrDefault = resumeRefusal.GetValueOrDefault();
						_parks.Refused(valueOrDefault);
					}
					if (text != null)
					{
						_activePrivacyContext = null;
						if (_inputMonitor != null)
						{
							_inputMonitor.SuppressKeys = false;
						}
						NotifyStateChanged(SessionState.Paused);
						Log("Privacy end: " + text + " at resume time; parked in Paused until real input", "HandlePrivacyEndAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\SessionCoordinator.cs", 2300);
						break;
					}
					if (flag2)
					{
						Log("Privacy resume aborted: segment already active seg=" + (num?.ToString() ?? "-") + " while privacy paused", "HandlePrivacyEndAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\SessionCoordinator.cs", 2306);
						break;
					}
					RestartClipRotationTimer();
					_systemMonitor?.LogInitialState();
					long num2 = (long)RecordingClock.Shared.NowMs();
					LogRow(new
					{
						type = "privacy_mask_end",
						timestamp = num2,
						maskedDisplay = context.DisplayIndex
					}, num2);
					_activePrivacyContext = null;
					if (_inputMonitor != null)
					{
						_inputMonitor.SuppressKeys = false;
					}
					NotifyStateChanged(SessionState.Recording);
					Log($"Privacy end: resumed recording on attempt {attempt}", "HandlePrivacyEndAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\SessionCoordinator.cs", 2323);
					break;
				}
				catch (Exception ex2) when (attempt < 3)
				{
					Log($"OnPrivacyEnd resume attempt {attempt} failed: {ex2.Message}", "HandlePrivacyEndAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\SessionCoordinator.cs", 2328);
					await Task.Delay(500, resumeToken).ConfigureAwait(continueOnCapturedContext: false);
				}
				catch (Exception ex3)
				{
					Log($"OnPrivacyEnd failed to resume recording: {ex3}", "HandlePrivacyEndAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\SessionCoordinator.cs", 2333);
					HandleCaptureFailureAsync(ex3);
					break;
				}
				finally
				{
					if (gateHeld)
					{
						_privacyTransitionGate.Release();
					}
				}
			}
		}
		catch (OperationCanceledException)
		{
		}
		catch (Exception value)
		{
			Log($"OnPrivacyEnd failed: {value}", "HandlePrivacyEndAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\SessionCoordinator.cs", 2352);
		}
	}

	private void StartTimers()
	{
		RestartClipRotationTimer();
		EnsureDiskCheckTimer();
	}

	private void StopTimers()
	{
		StopClipRotationTimer();
		lock (_timerSync)
		{
			_diskCheckTimer?.Stop();
			_diskCheckTimer?.Dispose();
			_diskCheckTimer = null;
		}
	}

	private void RestartClipRotationTimer(double? dueInMs = null)
	{
		lock (_timerSync)
		{
			int generation = Interlocked.Increment(ref _clipRotationTimerGeneration);
			_clipRotationTimer?.Stop();
			_clipRotationTimer?.Dispose();
			_clipRotationTimer = new System.Timers.Timer(dueInMs ?? ((double)(AppConfig.ClipDurationSeconds * 1000)));
			_clipRotationTimer.Elapsed += (object? _, ElapsedEventArgs _) =>
			{
				RotateClipSeamlessly(generation);
			};
			_clipRotationTimer.AutoReset = false;
			_clipRotationTimer.Start();
		}
	}

	private void StopClipRotationTimer()
	{
		lock (_timerSync)
		{
			Interlocked.Increment(ref _clipRotationTimerGeneration);
			_clipRotationTimer?.Stop();
			_clipRotationTimer?.Dispose();
			_clipRotationTimer = null;
		}
	}

	private void EnsureDiskCheckTimer()
	{
		lock (_timerSync)
		{
			if (_diskCheckTimer != null)
			{
				_diskCheckTimer.Start();
				return;
			}
			_diskCheckTimer = new System.Timers.Timer(AppConfig.DiskCheckIntervalSeconds * 1000);
			_diskCheckTimer.Elapsed += (object? _, ElapsedEventArgs _) =>
			{
				RunTimerCallback("disk_check", CheckDiskSpace);
			};
			_diskCheckTimer.AutoReset = true;
			_diskCheckTimer.Start();
		}
	}

	private void CheckDiskSpace()
	{
		long num = _storage.AvailableDiskSpaceBytes();
		if (State == SessionState.LowDisk)
		{
			long num2 = CurrentDiskThresholdBytes();
			if (_stoppedForLowDisk && num >= num2 && _auth.IsSignedIn && ShouldRecordNow())
			{
				_stoppedForLowDisk = false;
				Log($"Low disk recovered available_bytes={num} threshold_bytes={num2}", "CheckDiskSpace", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\SessionCoordinator.cs", 2431);
				_recoveryGate.RequestStart(RecoveryStartInitiator.DiskRecovery);
			}
			return;
		}
		SessionState state = State;
		if ((uint)(state - 1) > 2u)
		{
			return;
		}
		long num3 = CurrentDiskThresholdBytes();
		if (num >= num3)
		{
			return;
		}
		if (Volatile.Read(in _captureFailureHandling) != 0 || Interlocked.CompareExchange(ref _stopInProgress, 1, 0) != 0)
		{
			Log($"Low disk stop deferred because recorder cleanup is active available_bytes={num} threshold_bytes={num3}", "CheckDiskSpace", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\SessionCoordinator.cs", 2444);
			return;
		}
		try
		{
			CancelPendingPrivacyResume();
			_recoveryGate.StopSettleTimer();
			StopClipRotationTimer();
			RecordSessionEnded(SessionEndReason.LowDisk, cleanStop: false);
			DetachedRuntime runtime = DetachActiveRuntime();
			_stoppedForLowDisk = true;
			Log($"Low disk stop triggered available_bytes={num} threshold_bytes={num3}", "CheckDiskSpace", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\SessionCoordinator.cs", 2459);
			SetState(SessionState.LowDisk);
			Task.Run(async () =>
			{
				_ = 1;
				try
				{
					DisposeRuntimeMonitors();
					await StopDetachedCaptureAsync(runtime.Engine, "CheckDiskSpace").ConfigureAwait(continueOnCapturedContext: false);
					await SealDetachedSegmentAsync(runtime.Segment, "CheckDiskSpace", "low_disk").ConfigureAwait(continueOnCapturedContext: false);
					CompleteEventWriter(runtime.Router);
					DisposeDetachedAudio(runtime.SystemAudio, "CheckDiskSpace");
					ClearActiveClipMarker(runtime.SessionDir);
					runtime.UploadManager?.TriggerUpload();
				}
				finally
				{
					Interlocked.Exchange(ref _stopInProgress, 0);
					TryStartPendingSession();
				}
			});
		}
		catch
		{
			Interlocked.Exchange(ref _stopInProgress, 0);
			throw;
		}
	}

	private DetachedRuntime DetachActiveRuntime()
	{
		_inputMonitor?.Touchpad?.EndGestures();
		DetachEventSinks();
		PrivacyContext activePrivacyContext = _activePrivacyContext;
		RecorderWriteRouter writeRouter;
		SegmentSet segment;
		lock (_writeRoutingSync)
		{
			writeRouter = _writeRouter;
			segment = writeRouter?.Stop();
			_activeSegment = null;
			_writeRouter = null;
		}
		_frameState = null;
		_activePrivacyContext = null;
		SessionCaptureEngine engine = Interlocked.Exchange(ref _captureEngine, null);
		ISystemAudio systemAudio = Interlocked.Exchange(ref _systemAudio, null);
		UploadManager uploadManager = _uploadManager;
		string currentSessionDir = CurrentSessionDir;
		_activeDisplayPolicy = null;
		CurrentSessionDir = null;
		_currentSegmentIndex = 0;
		_sessionStartedRecorded = false;
		Telemetry.SetContext(null, null);
		return new DetachedRuntime(segment, engine, writeRouter, uploadManager, currentSessionDir, activePrivacyContext, systemAudio);
	}

	private async Task SealDetachedSegmentAsync(SegmentSet? segment, string logContext, string endReason, string? maskTrigger = null, string? maskDetail = null, string? source = null, Exception? error = null)
	{
		if (segment == null)
		{
			return;
		}
		try
		{
			await segment.SealAsync(endReason, maskTrigger, maskDetail, source, error).ConfigureAwait(continueOnCapturedContext: false);
		}
		catch (Exception ex)
		{
			Log($"{logContext} segment seal failed endReason={endReason}: {KuroLog.ExceptionSummary(ex)}", "SealDetachedSegmentAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\SessionCoordinator.cs", 2550);
		}
	}

	private async Task StopDetachedCaptureAsync(SessionCaptureEngine? engine, string logContext)
	{
		if (engine == null)
		{
			return;
		}
		try
		{
			await engine.StopCaptureAsync().ConfigureAwait(continueOnCapturedContext: false);
		}
		catch (Exception ex)
		{
			Log(logContext + " capture stop failed: " + KuroLog.ExceptionSummary(ex), "StopDetachedCaptureAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\SessionCoordinator.cs", 2569);
		}
	}

	private void DisposeDetachedAudio(ISystemAudio? audio, string logContext)
	{
		if (audio == null)
		{
			return;
		}
		try
		{
			audio.Dispose();
		}
		catch (Exception ex)
		{
			Log(logContext + " system audio dispose failed: " + KuroLog.ExceptionSummary(ex), "DisposeDetachedAudio", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\SessionCoordinator.cs", 2586);
		}
	}

	private void LogRow(object payload, long timestampMs)
	{
		_writeRouter?.Log(payload, timestampMs);
	}

	private static void CompleteEventWriter(RecorderWriteRouter? router)
	{
		router?.Complete(TimeSpan.FromSeconds(2.0));
	}

	private void SetState(SessionState newState)
	{
		if (State != newState)
		{
			SessionState state = State;
			State = newState;
			Log($"Session state changed {state}->{newState}", "SetState", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\SessionCoordinator.cs", 2606);
			NotifyStateChanged(newState);
			if ((newState == SessionState.Idle || (uint)(newState - 4) <= 1u) ? true : false)
			{
				TryTakeUpdateBoundary("state_" + newState.ToString().ToLowerInvariant());
			}
		}
	}

	private void NotifyStateChanged(SessionState newState)
	{
		ObservePark(newState);
		try
		{
			StateChanged?.Invoke(newState);
		}
		catch (Exception value)
		{
			Log($"StateChanged subscriber failed state={newState}: {value}", "NotifyStateChanged", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\SessionCoordinator.cs", 2623);
		}
	}

	private void ObservePark(SessionState state)
	{
		switch (state)
		{
		case SessionState.Paused:
			_parks.Paused(Path.GetFileName(CurrentSessionDir), _pausedFor ?? "idle");
			break;
		case SessionState.PrivacyPaused:
			_parks.Paused(Path.GetFileName(CurrentSessionDir), _activePrivacyContext?.Type ?? "privacy_mask");
			break;
		case SessionState.Recording:
			_parks.Recording();
			break;
		default:
			_parks.SessionEnded();
			break;
		}
	}

	private void RunTimerCallback(string name, Action callback)
	{
		try
		{
			callback();
		}
		catch (Exception ex)
		{
			Log($"{name} timer callback failed: {ex}", "RunTimerCallback", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\SessionCoordinator.cs", 2657);
			HandleCaptureFailureAsync(ex);
		}
	}

	private void DisposeRuntimeMonitors()
	{
		try
		{
			_systemMonitor?.Dispose();
		}
		catch (Exception value)
		{
			Log($"Runtime monitor dispose failed monitor=system_monitor: {value}", "DisposeRuntimeMonitors", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\SessionCoordinator.cs", 2670);
		}
		finally
		{
			_systemMonitor = null;
		}
		try
		{
			_idleManager?.Dispose();
		}
		catch (Exception value2)
		{
			Log($"Runtime monitor dispose failed monitor=idle_manager: {value2}", "DisposeRuntimeMonitors", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\SessionCoordinator.cs", 2674);
		}
		finally
		{
			_idleManager = null;
		}
		try
		{
			_microphoneMonitor?.Dispose();
		}
		catch (Exception value3)
		{
			Log($"Runtime monitor dispose failed monitor=microphone_monitor: {value3}", "DisposeRuntimeMonitors", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\SessionCoordinator.cs", 2678);
		}
		finally
		{
			_microphoneMonitor = null;
		}
		try
		{
			_cameraMonitor?.Dispose();
		}
		catch (Exception value4)
		{
			Log($"Runtime monitor dispose failed monitor=camera_monitor: {value4}", "DisposeRuntimeMonitors", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\SessionCoordinator.cs", 2682);
		}
		finally
		{
			_cameraMonitor = null;
		}
		InputMonitor inputMonitor = Interlocked.Exchange(ref _inputMonitor, null);
		try
		{
			inputMonitor?.Stop();
			inputMonitor?.Dispose();
		}
		catch (Exception value5)
		{
			Log($"Runtime monitor dispose failed monitor=input_monitor: {value5}", "DisposeRuntimeMonitors", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\SessionCoordinator.cs", 2693);
		}
		PrivacyManager? privacyManager = _privacyManager;
		_privacyManager = null;
		privacyManager?.Dispose();
	}

	private void MarkCurrentClipActive()
	{
		if (CurrentSessionDir == null)
		{
			return;
		}
		try
		{
			_storage.MarkSessionActive(CurrentSessionDir, _currentSegmentIndex);
		}
		catch (Exception ex)
		{
			Log($"Failed to mark active clip session={Path.GetFileName(CurrentSessionDir)} seg={_currentSegmentIndex}: {ex.Message}", "MarkCurrentClipActive", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\SessionCoordinator.cs", 2713);
		}
	}

	private void ClearActiveClipMarker(string? sessionDir = null)
	{
		string text = sessionDir ?? CurrentSessionDir;
		if (string.IsNullOrWhiteSpace(text))
		{
			return;
		}
		try
		{
			_storage.ClearSessionActive(text);
		}
		catch (Exception ex)
		{
			Log("Failed to clear active clip marker session=" + Path.GetFileName(text) + ": " + ex.Message, "ClearActiveClipMarker", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\SessionCoordinator.cs", 2731);
		}
	}

	private void DetachEventSinks()
	{
		_inputMonitor?.SetEventSink(null);
		_systemMonitor?.SetEventSink(null);
		_idleManager?.SetEventSink(null);
		_microphoneMonitor?.SetEventSink(null);
		_cameraMonitor?.SetEventSink(null);
	}

	private Task FinalizeStoppedSegmentAsync(SegmentSet segment, string endReason, Task? existingSealTask = null)
	{
		Task task = RunFinalizeStoppedSegmentAsync(segment, endReason, existingSealTask);
		_finalizesInFlight.TryAdd(task, 0);
		task.ContinueWith((Task key, object? state) => ((ConcurrentDictionary<Task, byte>)state).TryRemove(key, out var _), _finalizesInFlight, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
		return task;
	}

	private Task RunFinalizeStoppedSegmentAsync(SegmentSet segment, string endReason, Task? existingSealTask)
	{
		return Task.Run(async () =>
		{
			_ = 1;
			try
			{
				if (existingSealTask == null)
				{
					await segment.SealAsync(endReason).ConfigureAwait(continueOnCapturedContext: false);
				}
				else
				{
					await existingSealTask.ConfigureAwait(continueOnCapturedContext: false);
				}
				_uploadManager?.TriggerUpload();
			}
			catch (Exception value)
			{
				Log($"FinalizeStoppedSegmentAsync failed endReason={endReason}: {value}", "RunFinalizeStoppedSegmentAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\SessionCoordinator.cs", 2774);
			}
			finally
			{
				_writeRouter?.ClearPrevious(segment);
			}
		});
	}

	private void OnRecorderCaptureAccessDenied()
	{
		SessionState state = State;
		if ((uint)(state - 1) > 2u)
		{
			Log($"Capture access denied ignored because recorder is state={State}", "OnRecorderCaptureAccessDenied", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\SessionCoordinator.cs", 2788);
		}
	}

	private void OnDisplaySettingsChanged(object? sender, EventArgs e)
	{
		RemoteSessionProbe.BumpEnvironmentEpoch();
		CaptureHealthRegistry.ClearForRecoveryEvent(CaptureRecoveryEvent.TopologyChange);
		_recoveryGate.ClearHoldsForTopologyChange();
		SessionState state = State;
		if ((uint)state > 3u)
		{
			return;
		}
		if (Interlocked.Exchange(ref _displayChangeHandling, 1) != 0)
		{
			if (Interlocked.Exchange(ref _displayChangePending, 1) == 0)
			{
				Log("Display settings change coalesced into active handling; re-evaluation queued", "OnDisplaySettingsChanged", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\SessionCoordinator.cs", 2818);
			}
			return;
		}
		Task.Run(async () =>
		{
			_ = 1;
			try
			{
				do
				{
					Interlocked.Exchange(ref _displayChangePending, 0);
					await Task.Delay(500).ConfigureAwait(continueOnCapturedContext: false);
					SessionState state2 = State;
					if ((uint)(state2 - 1) <= 2u)
					{
						await HandleTopologyChangeAsync().ConfigureAwait(continueOnCapturedContext: false);
					}
					else if (State == SessionState.Idle && _auth.IsSignedIn && ShouldRecordNow() && Volatile.Read(in _updateRequiredRequested) == 0 && Volatile.Read(in _stopInProgress) == 0 && Volatile.Read(in _captureFailureHandling) == 0)
					{
						Log("Display topology changed while idle; attempting recorder start", "OnDisplaySettingsChanged", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\SessionCoordinator.cs", 2845);
						_recoveryGate.RequestStart(RecoveryStartInitiator.DisplayChange);
					}
					else if (State == SessionState.Idle)
					{
						try
						{
							DisplayTopologyPolicyResult displayTopologyPolicyResult = DisplayTopologyPolicy.Evaluate(DisplayTopologyPolicy.ProfileForIndex(0));
							_unsupportedTopologyReason = (displayTopologyPolicyResult.IsSupported ? null : (displayTopologyPolicyResult.UnsupportedReason ?? "unsupported_display_topology"));
							if (displayTopologyPolicyResult.IsSupported)
							{
								_lastUnsupportedLogSignature = null;
							}
						}
						catch
						{
						}
					}
				}
				while (Interlocked.Exchange(ref _displayChangePending, 0) != 0);
			}
			finally
			{
				Interlocked.Exchange(ref _displayChangeHandling, 0);
			}
			if (Volatile.Read(in _displayChangePending) != 0)
			{
				Log("Display settings change arrived during handler teardown; re-evaluating", "OnDisplaySettingsChanged", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\SessionCoordinator.cs", 2882);
				OnDisplaySettingsChanged(null, EventArgs.Empty);
			}
		});
	}

	private void OnRecorderCaptureFailed(Exception ex)
	{
		HandleCaptureFailureAsync(ex);
	}

	private async Task HandleCaptureFailureAsync(Exception ex)
	{
		if (Interlocked.Exchange(ref _captureFailureHandling, 1) != 0)
		{
			Log("Capture failure ignored because another recovery is active: " + ex.Message, "HandleCaptureFailureAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\SessionCoordinator.cs", 2897);
			return;
		}
		try
		{
			if (State == SessionState.UpdateRequired)
			{
				Log("Capture failure ignored because update is required: " + ex.Message, "HandleCaptureFailureAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\SessionCoordinator.cs", 2905);
				return;
			}
			if (Volatile.Read(in _stopInProgress) != 0)
			{
				Log("Capture failure ignored while recorder stop is in progress: " + ex.Message, "HandleCaptureFailureAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\SessionCoordinator.cs", 2911);
				return;
			}
			if (State == SessionState.Idle && _activeSegment == null && _inputMonitor == null && _captureEngine == null && _writeRouter == null && _frameState == null && _systemAudio == null && CurrentSessionDir == null)
			{
				Log("Capture failure ignored because no active recorder runtime remains: " + ex.Message, "HandleCaptureFailureAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\SessionCoordinator.cs", 2924);
				_recoveryGate.RecordFailure(ex);
				return;
			}
			bool accessDenied = CaptureAccessDeniedClassifier.IsAccessDenied(ex);
			if (accessDenied)
			{
				Count.CaptureDenied(TelemetryMap.DenialClassFor(ex), 1L);
			}
			_recoveryGate.OpenUnavailabilityInterval(accessDenied ? "access_denied" : "capture_failure", ex);
			_recoveryGate.StopSettleTimer();
			if (!accessDenied)
			{
				RecordAdaptiveCaptureFailure(ex);
			}
			CancelPendingPrivacyResume();
			StopTimers();
			RecordSessionEnded(accessDenied ? SessionEndReason.CaptureLost : SessionEndReason.Error, cleanStop: false);
			DetachedRuntime runtime = DetachActiveRuntime();
			await Task.Run((Action)DisposeRuntimeMonitors).ConfigureAwait(continueOnCapturedContext: false);
			_stoppedForLowDisk = false;
			SetState(SessionState.Idle);
			await StopDetachedCaptureAsync(runtime.Engine, accessDenied ? "Capture access denied" : "Capture failure").ConfigureAwait(continueOnCapturedContext: false);
			await SealDetachedSegmentAsync(runtime.Segment, accessDenied ? "Capture access denied" : "Capture failure", accessDenied ? "capture_access_denied" : "capture_failure", null, null, "windows_capture", ex).ConfigureAwait(continueOnCapturedContext: false);
			CompleteEventWriter(runtime.Router);
			await Task.Run(() =>
			{
				DisposeDetachedAudio(runtime.SystemAudio, accessDenied ? "Capture access denied" : "Capture failure");
			}).ConfigureAwait(continueOnCapturedContext: false);
			ClearActiveClipMarker(runtime.SessionDir);
			runtime.UploadManager?.TriggerUpload();
			_recoveryGate.RecordFailure(ex);
		}
		finally
		{
			Interlocked.Exchange(ref _captureFailureHandling, 0);
			TryStartPendingSession();
		}
	}

	private void QueueStartAfterCleanup(string reason)
	{
		Interlocked.Exchange(ref _pendingStartAfterCleanup, 1);
		Log("StartSession queued while " + reason + " is cleaning up", "QueueStartAfterCleanup", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\SessionCoordinator.cs", 3001);
	}

	private void TryStartPendingSession()
	{
		if (Interlocked.CompareExchange(ref _pendingStartAfterCleanup, 0, 1) == 1)
		{
			if (Volatile.Read(in _stopInProgress) != 0 || Volatile.Read(in _captureFailureHandling) != 0)
			{
				Interlocked.Exchange(ref _pendingStartAfterCleanup, 1);
			}
			else if (State != SessionState.Idle || !_auth.IsSignedIn || Volatile.Read(in _updateRequiredRequested) != 0 || !ShouldRecordNow())
			{
				Log($"Queued session discarded state={State} signed_in={_auth.IsSignedIn} update_required={Volatile.Read(in _updateRequiredRequested) != 0} should_record={ShouldRecordNow()}", "TryStartPendingSession", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\SessionCoordinator.cs", 3023);
			}
			else
			{
				Log("Starting queued session after recorder cleanup", "TryStartPendingSession", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\SessionCoordinator.cs", 3027);
				_recoveryGate.RequestStart(RecoveryStartInitiator.PendingQueue);
			}
		}
	}

	private bool TryClearNonIdleStateForRecovery()
	{
		if (State == SessionState.Idle)
		{
			return true;
		}
		SessionState state = State;
		bool flag = (uint)(state - 1) <= 2u;
		if (!flag || Volatile.Read(in _captureEngine) != null || Volatile.Read(in _startInProgress) != 0 || Volatile.Read(in _stopInProgress) != 0 || Volatile.Read(in _captureFailureHandling) != 0)
		{
			KuroLog.Capture($"Capture recovery superseded by state={State}", "TryClearNonIdleStateForRecovery", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\SessionCoordinator.cs", 3065);
			return false;
		}
		KuroLog.Capture($"Capture recovery overriding phantom state={State}: no capture engine is alive; resetting to Idle", "TryClearNonIdleStateForRecovery", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\SessionCoordinator.cs", 3069);
		SetState(SessionState.Idle);
		return true;
	}

	private void MarkAdaptiveProfileStable()
	{
		DisplayTopologyPolicyResult activeDisplayPolicy = _activeDisplayPolicy;
		if ((object)activeDisplayPolicy != null)
		{
			_adaptiveProfileStore.MarkStable(activeDisplayPolicy);
		}
	}

	private static IReadOnlyList<string> CurrentDisplayKeys()
	{
		try
		{
			IReadOnlyList<MonitorTable.Entry> screens = MonitorTable.Refresh().Screens;
			List<string> list = new List<string>(screens.Count);
			foreach (MonitorTable.Entry item in screens)
			{
				list.Add(CaptureHealthRegistry.KeyFor(item.Screen));
			}
			return list;
		}
		catch
		{
			return Array.Empty<string>();
		}
	}

	private void OnAvailabilitySessionBecameActive(SessionActiveReason reason)
	{
		RemoteSessionProbe.BumpEnvironmentEpoch();
		string value;
		CaptureRecoveryEvent reason2;
		RecoveryOsEvent osEvent;
		switch (reason)
		{
		case SessionActiveReason.Unlock:
			value = "unlock";
			reason2 = CaptureRecoveryEvent.Unlock;
			osEvent = RecoveryOsEvent.Unlock;
			break;
		case SessionActiveReason.Logon:
			value = "logon";
			reason2 = CaptureRecoveryEvent.Logon;
			osEvent = RecoveryOsEvent.Logon;
			break;
		case SessionActiveReason.ConsoleConnect:
			value = "console_connect";
			reason2 = CaptureRecoveryEvent.Logon;
			osEvent = RecoveryOsEvent.Connect;
			break;
		default:
			value = "remote_connect";
			reason2 = CaptureRecoveryEvent.Logon;
			osEvent = RecoveryOsEvent.Connect;
			break;
		}
		CaptureHealthRegistry.ClearForRecoveryEvent(reason2);
		_recoveryGate.NoteOsEvent();
		_recoveryGate.ClearHoldsForOsEvent(osEvent);
		bool flag = false;
		if (!_appShutdownRequested && ShouldRecordNow())
		{
			if (State == SessionState.Idle)
			{
				flag = true;
			}
			else if (_recoveryGate.HasFailureState && TryClearNonIdleStateForRecovery())
			{
				flag = true;
			}
		}
		if (flag && CurrentCaptureRecoveryFlags().LegacyCadence)
		{
			flag = false;
		}
		KuroLog.Capture($"os_recovery event={value} action={(flag ? "start" : "none")} state={State}", "OnAvailabilitySessionBecameActive", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\SessionCoordinator.cs", 3174);
		if (flag)
		{
			Task.Run(() =>
			{
				_recoveryGate.RequestStart(RecoveryStartInitiator.OsEvent);
			});
		}
	}

	private void OnAvailabilityResumed(bool userPresent)
	{
		CaptureHealthRegistry.ClearForRecoveryEvent(CaptureRecoveryEvent.Resume);
		_recoveryGate.NoteOsEvent();
		_recoveryGate.ClearHoldsForOsEvent(RecoveryOsEvent.Resume);
		string value = (userPresent ? "user" : "auto");
		if (!_availabilityEvents.SessionInteractive)
		{
			KuroLog.Capture($"os_recovery event=resume action=defer state={State} kind={value}", "OnAvailabilityResumed", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\SessionCoordinator.cs", 3198);
			return;
		}
		if (!userPresent || _appShutdownRequested || State != SessionState.Idle || !ShouldRecordNow() || CurrentCaptureRecoveryFlags().LegacyCadence)
		{
			KuroLog.Capture($"os_recovery event=resume action=none state={State} kind={value}", "OnAvailabilityResumed", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\SessionCoordinator.cs", 3219);
			return;
		}
		KuroLog.Capture($"os_recovery event=resume action=start state={State} kind={value}", "OnAvailabilityResumed", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\SessionCoordinator.cs", 3223);
		CancellationTokenSource cancellationTokenSource = new CancellationTokenSource();
		CancellationToken token = cancellationTokenSource.Token;
		CancellationTokenSource cancellationTokenSource2 = Interlocked.Exchange(ref _resumeSettleCts, cancellationTokenSource);
		if (cancellationTokenSource2 != null)
		{
			try
			{
				cancellationTokenSource2.Cancel();
			}
			catch
			{
			}
			cancellationTokenSource2.Dispose();
		}
		Task.Run(async () =>
		{
			try
			{
				await Task.Delay(5000, token).ConfigureAwait(continueOnCapturedContext: false);
			}
			catch (OperationCanceledException)
			{
				return;
			}
			if (!token.IsCancellationRequested && !_appShutdownRequested && State == SessionState.Idle && _availabilityEvents.SessionInteractive)
			{
				_recoveryGate.RequestStart(RecoveryStartInitiator.OsEvent);
			}
		});
	}

	private CancellationToken ReplacePrivacyResumeCts()
	{
		CancellationTokenSource cancellationTokenSource = new CancellationTokenSource();
		CancellationToken token = cancellationTokenSource.Token;
		CancellationTokenSource cancellationTokenSource2 = Interlocked.Exchange(ref _privacyResumeCts, cancellationTokenSource);
		if (cancellationTokenSource2 != null)
		{
			try
			{
				cancellationTokenSource2.Cancel();
			}
			catch
			{
			}
			cancellationTokenSource2.Dispose();
		}
		return token;
	}

	private void CancelPendingPrivacyResume()
	{
		CancellationTokenSource cancellationTokenSource = Interlocked.Exchange(ref _privacyResumeCts, null);
		if (cancellationTokenSource != null)
		{
			try
			{
				cancellationTokenSource.Cancel();
			}
			catch
			{
			}
			cancellationTokenSource.Dispose();
		}
	}

	private void CancelPendingResumeSettle()
	{
		CancellationTokenSource cancellationTokenSource = Interlocked.Exchange(ref _resumeSettleCts, null);
		if (cancellationTokenSource != null)
		{
			try
			{
				cancellationTokenSource.Cancel();
			}
			catch
			{
			}
			cancellationTokenSource.Dispose();
		}
	}

	private static void Log(string message, [CallerMemberName] string memberName = "", [CallerFilePath] string filePath = "", [CallerLineNumber] int lineNumber = 0)
	{
		KuroLog.Recording(message, memberName, filePath, lineNumber);
	}
}
