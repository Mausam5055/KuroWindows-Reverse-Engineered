using System;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using ClientTelemetry;
using KuroWindows.RecorderCore.Clock;

namespace KuroWindows.Input;

public sealed class IdleManager : IDisposable
{
	private readonly SystemAvailabilityEvents _availabilityEvents;

	private IEventSink? _eventSink;

	private int _checkIdleFailures;

	private int _isIdle;

	private bool _isSystemInactive;

	private int _latchedInputLine;

	private int _inactiveInputReported;

	private System.Timers.Timer? _idleTimer;

	public Action<string>? OnIdle;

	public Action? OnResume;

	public Action<ResumeRefusal>? OnInputRefused;

	public bool IsIdle => Volatile.Read(in _isIdle) != 0;

	public IdleManager(SystemAvailabilityEvents availabilityEvents)
	{
		_availabilityEvents = availabilityEvents;
	}

	public void SetEventSink(IEventSink? sink)
	{
		Volatile.Write(ref _eventSink, sink);
	}

	public void Start()
	{
		_availabilityEvents.SessionBecameInactiveGate += OnSessionBecameInactive;
		_availabilityEvents.SessionBecameActiveGate += OnSessionBecameActive;
		_availabilityEvents.SuspendingGate += TriggerSuspend;
		_availabilityEvents.ResumedGate += TriggerSystemActive;
		if (_availabilityEvents.SuspendLatched)
		{
			_isSystemInactive = true;
			Volatile.Write(ref _inactiveInputReported, 0);
		}
		long num = SystemAvailabilityEvents.LastInputAgeMs();
		if (_isSystemInactive || num < 0 || num > 30000)
		{
			Volatile.Write(ref _isIdle, 1);
			long num2 = (long)RecordingClock.Shared.NowMs();
			Log(new
			{
				type = "idle",
				reason = "already_idle",
				timestamp = num2
			}, num2);
			SafeInvoke(OnIdle, "idle", "idle/already_idle");
		}
		_idleTimer = new System.Timers.Timer(5000.0);
		_idleTimer.Elapsed += (object? _, ElapsedEventArgs _) =>
		{
			try
			{
				WriteLatchedInputLine();
				CheckIdle();
				int num3 = Interlocked.Exchange(ref _checkIdleFailures, 0);
				if (num3 > 0)
				{
					KuroLog.SystemEvent($"IdleManager CheckIdle recovered failures={num3}", "Start", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Input\\IdleManager.cs", 124);
				}
			}
			catch (Exception value)
			{
				if (Interlocked.Increment(ref _checkIdleFailures) == 1)
				{
					KuroLog.SystemEvent($"IdleManager CheckIdle failed: {value}", "Start", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Input\\IdleManager.cs", 133);
				}
			}
		};
		_idleTimer.Start();
	}

	public void UserDidInteract()
	{
		if (_availabilityEvents.SuspendLatched)
		{
			if (Interlocked.CompareExchange(ref _latchedInputLine, 1, 0) == 0)
			{
				OnInputRefused?.Invoke(ResumeRefusal.SuspendLatch);
			}
		}
		else if (_isSystemInactive)
		{
			if (Interlocked.CompareExchange(ref _inactiveInputReported, 1, 0) == 0)
			{
				OnInputRefused?.Invoke(ResumeRefusal.SystemInactive);
			}
		}
		else if (Interlocked.CompareExchange(ref _isIdle, 0, 1) == 1)
		{
			Task.Run(() =>
			{
				SafeInvoke(OnResume, "resume");
				long num = (long)RecordingClock.Shared.NowMs();
				Log(new
				{
					type = "resume",
					timestamp = num
				}, num);
			});
		}
	}

	private void WriteLatchedInputLine()
	{
		if (Interlocked.CompareExchange(ref _latchedInputLine, 2, 1) == 1)
		{
			KuroLog.Capture("os_recovery event=input_while_suspend_latched", "WriteLatchedInputLine", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Input\\IdleManager.cs", 189);
		}
	}

	internal void CheckIdle()
	{
		if (!IsIdle && !_availabilityEvents.SuspendFenceArmed)
		{
			long num = SystemAvailabilityEvents.LastInputAgeMs();
			if (num < 0 || num > 30000)
			{
				Volatile.Write(ref _isIdle, 1);
				long num2 = (long)RecordingClock.Shared.NowMs();
				Log(new
				{
					type = "idle",
					idleMs = num,
					timestamp = num2
				}, num2);
				SafeInvoke(OnIdle, "idle", "idle/timer");
			}
		}
	}

	internal void TriggerSuspend()
	{
		_isSystemInactive = true;
		Volatile.Write(ref _inactiveInputReported, 0);
		Interlocked.Exchange(ref _latchedInputLine, 0);
		long num = (_availabilityEvents.SuspendFenceArmed ? _availabilityEvents.SuspendFenceWallMs : _availabilityEvents.LastSuspendAtWallMs);
		if (num <= 0 || num == long.MaxValue)
		{
			num = (long)RecordingClock.Shared.NowMs();
		}
		bool flag = !IsIdle;
		KuroLog.Capture($"os_recovery event=suspend_fence wall_ms={num} owned_seal={(flag ? "true" : "false")}", "TriggerSuspend", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Input\\IdleManager.cs", 236);
		if (flag)
		{
			Volatile.Write(ref _isIdle, 1);
			Log(new
			{
				type = "idle",
				reason = "suspend",
				timestamp = num
			}, num);
			SafeInvoke(OnIdle, "system_sleep", "idle/suspend");
		}
	}

	internal void RearmIdleForRetry()
	{
		Volatile.Write(ref _isIdle, 1);
	}

	internal void TriggerSystemActive(bool userPresent)
	{
		if (userPresent)
		{
			_isSystemInactive = false;
		}
	}

	internal void OnSessionBecameInactive(SessionInactiveReason reason)
	{
		_isSystemInactive = true;
		Volatile.Write(ref _inactiveInputReported, 0);
		if (!IsIdle)
		{
			string text = LegacySwitchReasonName(reason);
			Volatile.Write(ref _isIdle, 1);
			try
			{
				long num = (long)RecordingClock.Shared.NowMs();
				Log(new
				{
					type = "idle",
					reason = text.ToLowerInvariant(),
					timestamp = num
				}, num);
			}
			catch (Exception ex)
			{
				KuroLog.SystemEvent("idle event log failed: " + ex.GetType().Name + ": " + ex.Message, "OnSessionBecameInactive", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Input\\IdleManager.cs", 294);
			}
			SafeInvoke(OnIdle, SealReasonForSwitch(reason), "idle/" + text);
		}
	}

	private void OnSessionBecameActive(SessionActiveReason reason)
	{
		_isSystemInactive = false;
	}

	private static string LegacySwitchReasonName(SessionInactiveReason reason)
	{
		return reason switch
		{
			SessionInactiveReason.Lock => "SessionLock", 
			SessionInactiveReason.Logoff => "SessionLogoff", 
			SessionInactiveReason.ConsoleDisconnect => "ConsoleDisconnect", 
			SessionInactiveReason.RemoteDisconnect => "RemoteDisconnect", 
			_ => reason.ToString(), 
		};
	}

	private static string SealReasonForSwitch(SessionInactiveReason reason)
	{
		return reason switch
		{
			SessionInactiveReason.Lock => "screen_lock", 
			SessionInactiveReason.Logoff => "session_logoff", 
			SessionInactiveReason.ConsoleDisconnect => "session_disconnect", 
			SessionInactiveReason.RemoteDisconnect => "session_disconnect", 
			_ => "idle", 
		};
	}

	private void Log(object payload, long timestampMs)
	{
		Volatile.Read(in _eventSink)?.Log(payload, timestampMs);
	}

	private static void SafeInvoke(Action? action, string name)
	{
		try
		{
			action?.Invoke();
		}
		catch (Exception value)
		{
			KuroLog.SystemEvent($"IdleManager {name} callback failed: {value}", "SafeInvoke", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Input\\IdleManager.cs", 338);
		}
	}

	private static void SafeInvoke(Action<string>? action, string sealReason, string name)
	{
		try
		{
			action?.Invoke(sealReason);
		}
		catch (Exception value)
		{
			KuroLog.SystemEvent($"IdleManager {name} callback failed: {value}", "SafeInvoke", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Input\\IdleManager.cs", 352);
		}
	}

	public void Dispose()
	{
		_idleTimer?.Stop();
		_idleTimer?.Dispose();
		WriteLatchedInputLine();
		_availabilityEvents.SessionBecameInactiveGate -= OnSessionBecameInactive;
		_availabilityEvents.SessionBecameActiveGate -= OnSessionBecameActive;
		_availabilityEvents.SuspendingGate -= TriggerSuspend;
		_availabilityEvents.ResumedGate -= TriggerSystemActive;
	}
}
