using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Windows.Forms;
using KuroWindows.RecorderCore.Clock;
using Microsoft.Win32;

namespace KuroWindows.Input;

public sealed class SystemAvailabilityEvents : IDisposable
{
	private struct MSG
	{
		public nint hwnd;

		public uint message;

		public nint wParam;

		public nint lParam;

		public uint time;

		public int x;

		public int y;
	}

	private struct LASTINPUTINFO
	{
		public uint cbSize;

		public uint dwTime;
	}

	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	private struct WTSINFOEX_LEVEL1
	{
		public uint SessionId;

		public int SessionState;

		public int SessionFlags;

		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 33)]
		public string WinStationName;

		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 21)]
		public string UserName;

		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 18)]
		public string DomainName;

		public long LogonTime;

		public long ConnectTime;

		public long DisconnectTime;

		public long LastInputTime;

		public long CurrentTime;

		public uint IncomingBytes;

		public uint OutgoingBytes;

		public uint IncomingFrames;

		public uint OutgoingFrames;

		public uint IncomingCompressedBytes;

		public uint OutgoingCompressedBytes;
	}

	private struct WTSINFOEX
	{
		public uint Level;

		public WTSINFOEX_LEVEL1 Data;
	}

	private sealed class PowerWindow : NativeWindow
	{
		private readonly SystemAvailabilityEvents _owner;

		internal PowerWindow(SystemAvailabilityEvents owner)
		{
			_owner = owner;
		}

		internal void Create()
		{
			CreateHandle(new CreateParams
			{
				Parent = new IntPtr(-3)
			});
		}

		protected override void WndProc(ref Message m)
		{
			if (m.Msg == 536)
			{
				try
				{
					switch (((IntPtr)m.WParam).ToInt32())
					{
					case 4:
						_owner.ArmSuspendFence((long)RecordingClock.Shared.NowMs());
						_owner._signals.Writer.TryWrite(SystemAvailabilitySignal.Suspend);
						break;
					case 7:
						_owner._signals.Writer.TryWrite(SystemAvailabilitySignal.ResumeUser);
						break;
					case 18:
						_owner._signals.Writer.TryWrite(SystemAvailabilitySignal.ResumeAuto);
						break;
					}
				}
				catch
				{
				}
			}
			base.WndProc(ref m);
		}
	}

	private const uint WM_POWERBROADCAST = 536u;

	private const int PBT_APMSUSPEND = 4;

	private const int PBT_APMRESUMESUSPEND = 7;

	private const int PBT_APMRESUMEAUTOMATIC = 18;

	private const uint WM_QUIT = 18u;

	private const uint DEVICE_NOTIFY_WINDOW_HANDLE = 0u;

	private const int WtsCurrentSession = -1;

	private const int WtsSessionInfoEx = 25;

	private const int WtsSessionStateLock = 0;

	private readonly Channel<SystemAvailabilitySignal> _signals;

	private readonly Task _consumer;

	private readonly Func<long> _nowUtcMs;

	private readonly bool _attachedToOsSources;

	private volatile bool _sessionInteractive;

	private long _lastEventObservedUtcMs;

	private int _disposed;

	private long _suspendFenceWallMs = long.MaxValue;

	private long _lastSuspendAtWallMs;

	private int _suspendLatched;

	private Thread? _powerThread;

	private uint _powerThreadId;

	private nint _powerNotifHandle;

	public long SuspendFenceWallMs => Interlocked.Read(in _suspendFenceWallMs);

	public bool SuspendFenceArmed => SuspendFenceWallMs != long.MaxValue;

	public long LastSuspendAtWallMs => Interlocked.Read(in _lastSuspendAtWallMs);

	public bool SuspendLatched => Volatile.Read(in _suspendLatched) != 0;

	public bool SessionInteractive => _sessionInteractive;

	public long LastEventObservedUtcMs => Volatile.Read(in _lastEventObservedUtcMs);

	internal Task ConsumerCompletion => _consumer;

	public event Action<SessionInactiveReason>? SessionBecameInactiveGate;

	public event Action<SessionActiveReason>? SessionBecameActiveGate;

	public event Action? SuspendingGate;

	public event Action<bool>? ResumedGate;

	public event Action<SessionInactiveReason>? SessionBecameInactive;

	public event Action<SessionActiveReason>? SessionBecameActive;

	public event Action? Suspending;

	public event Action<bool>? Resumed;

	[DllImport("user32.dll", SetLastError = true)]
	private static extern int GetMessage(out MSG msg, nint hwnd, uint min, uint max);

	[DllImport("user32.dll")]
	private static extern bool TranslateMessage(ref MSG msg);

	[DllImport("user32.dll")]
	private static extern nint DispatchMessage(ref MSG msg);

	[DllImport("user32.dll")]
	private static extern bool PostThreadMessage(uint tid, uint msg, nint w, nint l);

	[DllImport("kernel32.dll")]
	private static extern uint GetCurrentThreadId();

	[DllImport("user32.dll")]
	private static extern nint RegisterSuspendResumeNotification(nint hRecipient, uint Flags);

	[DllImport("user32.dll")]
	private static extern bool UnregisterSuspendResumeNotification(nint handle);

	[DllImport("user32.dll")]
	private static extern bool GetLastInputInfo(ref LASTINPUTINFO info);

	[DllImport("kernel32.dll")]
	private static extern uint GetTickCount();

	[DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern bool WTSQuerySessionInformation(nint hServer, int sessionId, int wtsInfoClass, out nint buffer, out uint bytesReturned);

	[DllImport("wtsapi32.dll")]
	private static extern void WTSFreeMemory(nint memory);

	internal void ArmSuspendFence(long wallMs)
	{
		long num;
		do
		{
			num = Interlocked.Read(in _suspendFenceWallMs);
		}
		while (wallMs < num && Interlocked.CompareExchange(ref _suspendFenceWallMs, wallMs, num) != num);
		Interlocked.Exchange(ref _lastSuspendAtWallMs, wallMs);
		Interlocked.Exchange(ref _suspendLatched, 1);
	}

	public void ClearSuspendHold(string reason)
	{
		bool flag = Interlocked.Exchange(ref _suspendLatched, 0) != 0;
		bool flag2 = Interlocked.Exchange(ref _suspendFenceWallMs, long.MaxValue) != long.MaxValue;
		if (flag | flag2)
		{
			KuroLog.LocalOnly("system", "suspend hold cleared reason=" + reason, "ClearSuspendHold", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Input\\SystemAvailabilityEvents.cs", 236);
		}
	}

	public SystemAvailabilityEvents()
		: this(attachToOsSources: true, null, null)
	{
	}

	internal SystemAvailabilityEvents(bool attachToOsSources, Func<bool>? interactiveSeed, Func<long>? nowUtcMs)
	{
		_nowUtcMs = nowUtcMs ?? ((Func<long>)(() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
		_signals = Channel.CreateUnbounded<SystemAvailabilitySignal>(new UnboundedChannelOptions
		{
			SingleReader = true,
			SingleWriter = false
		});
		bool flag;
		try
		{
			flag = interactiveSeed?.Invoke() ?? QueryWtsSessionInteractive();
		}
		catch
		{
			flag = true;
		}
		_sessionInteractive = flag;
		KuroLog.LocalOnly("system", "system availability seeded interactive=" + (flag ? "true" : "false"), ".ctor", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Input\\SystemAvailabilityEvents.cs", 323);
		_consumer = Task.Run((Func<Task?>)ConsumeAsync);
		_attachedToOsSources = attachToOsSources;
		if (attachToOsSources)
		{
			SystemEvents.SessionSwitch += OnSessionSwitch;
			_powerThread = new Thread(PowerThreadProc)
			{
				IsBackground = true,
				Name = "KuroPower"
			};
			_powerThread.SetApartmentState(ApartmentState.STA);
			_powerThread.Start();
		}
	}

	public static bool QueryWtsSessionInteractive()
	{
		nint buffer = IntPtr.Zero;
		try
		{
			if (!WTSQuerySessionInformation(IntPtr.Zero, -1, 25, out buffer, out var bytesReturned) || buffer == IntPtr.Zero || bytesReturned < (uint)Marshal.SizeOf<WTSINFOEX>())
			{
				return true;
			}
			WTSINFOEX wTSINFOEX = Marshal.PtrToStructure<WTSINFOEX>(buffer);
			if (wTSINFOEX.Level != 1)
			{
				return true;
			}
			return wTSINFOEX.Data.SessionFlags != 0;
		}
		catch
		{
			return true;
		}
		finally
		{
			if (buffer != IntPtr.Zero)
			{
				try
				{
					WTSFreeMemory(buffer);
				}
				catch
				{
				}
			}
		}
	}

	public static long LastInputAgeMs()
	{
		try
		{
			LASTINPUTINFO info = new LASTINPUTINFO
			{
				cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>()
			};
			if (!GetLastInputInfo(ref info))
			{
				return -1L;
			}
			return GetTickCount() - info.dwTime;
		}
		catch
		{
			return -1L;
		}
	}

	private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
	{
		try
		{
			SystemAvailabilitySignal? systemAvailabilitySignal = e.Reason switch
			{
				SessionSwitchReason.SessionLock => (SystemAvailabilitySignal?)SystemAvailabilitySignal.SessionLock, 
				SessionSwitchReason.SessionLogoff => SystemAvailabilitySignal.SessionLogoff, 
				SessionSwitchReason.ConsoleDisconnect => SystemAvailabilitySignal.ConsoleDisconnect, 
				SessionSwitchReason.RemoteDisconnect => SystemAvailabilitySignal.RemoteDisconnect, 
				SessionSwitchReason.SessionUnlock => SystemAvailabilitySignal.SessionUnlock, 
				SessionSwitchReason.SessionLogon => SystemAvailabilitySignal.SessionLogon, 
				SessionSwitchReason.ConsoleConnect => SystemAvailabilitySignal.ConsoleConnect, 
				SessionSwitchReason.RemoteConnect => SystemAvailabilitySignal.RemoteConnect, 
				_ => null, 
			};
			if (systemAvailabilitySignal.HasValue)
			{
				SystemAvailabilitySignal valueOrDefault = systemAvailabilitySignal.GetValueOrDefault();
				_signals.Writer.TryWrite(valueOrDefault);
			}
		}
		catch
		{
		}
	}

	private async Task ConsumeAsync()
	{
		_ = 1;
		try
		{
			await foreach (SystemAvailabilitySignal item in _signals.Reader.ReadAllAsync().ConfigureAwait(continueOnCapturedContext: false))
			{
				try
				{
					Dispatch(item);
				}
				catch (Exception ex)
				{
					KuroLog.SystemEvent($"SystemAvailabilityEvents dispatch failed signal={item}: {ex.GetType().Name}: {ex.Message}", "ConsumeAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Input\\SystemAvailabilityEvents.cs", 444);
				}
			}
		}
		catch (Exception ex2)
		{
			KuroLog.SystemEvent("SystemAvailabilityEvents consumer failed: " + ex2.GetType().Name + ": " + ex2.Message, "ConsumeAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Input\\SystemAvailabilityEvents.cs", 451);
		}
	}

	private void Dispatch(SystemAvailabilitySignal signal)
	{
		Volatile.Write(ref _lastEventObservedUtcMs, _nowUtcMs());
		switch (signal)
		{
		case SystemAvailabilitySignal.SessionLock:
			DispatchInactive(SessionInactiveReason.Lock);
			break;
		case SystemAvailabilitySignal.SessionLogoff:
			DispatchInactive(SessionInactiveReason.Logoff);
			break;
		case SystemAvailabilitySignal.ConsoleDisconnect:
			DispatchInactive(SessionInactiveReason.ConsoleDisconnect);
			break;
		case SystemAvailabilitySignal.RemoteDisconnect:
			DispatchInactive(SessionInactiveReason.RemoteDisconnect);
			break;
		case SystemAvailabilitySignal.SessionUnlock:
			DispatchActive(SessionActiveReason.Unlock, "sessionunlock");
			break;
		case SystemAvailabilitySignal.SessionLogon:
			DispatchActive(SessionActiveReason.Logon, "sessionlogon");
			break;
		case SystemAvailabilitySignal.ConsoleConnect:
			DispatchActive(SessionActiveReason.ConsoleConnect, "consoleconnect");
			break;
		case SystemAvailabilitySignal.RemoteConnect:
			DispatchActive(SessionActiveReason.RemoteConnect, "remoteconnect");
			break;
		case SystemAvailabilitySignal.Suspend:
			InvokeEach(SuspendingGate, "SuspendingGate");
			InvokeEach(Suspending, "Suspending");
			break;
		case SystemAvailabilitySignal.ResumeUser:
			ClearSuspendHold("resume_user");
			InvokeEach(ResumedGate, arg: true, "ResumedGate");
			InvokeEach(Resumed, arg: true, "Resumed");
			break;
		case SystemAvailabilitySignal.ResumeAuto:
			InvokeEach(ResumedGate, arg: false, "ResumedGate");
			InvokeEach(Resumed, arg: false, "Resumed");
			break;
		}
	}

	private void DispatchInactive(SessionInactiveReason reason)
	{
		_sessionInteractive = false;
		InvokeEach(SessionBecameInactiveGate, reason, "SessionBecameInactiveGate");
		InvokeEach(SessionBecameInactive, reason, "SessionBecameInactive");
	}

	private void DispatchActive(SessionActiveReason reason, string unlockLogReason)
	{
		_sessionInteractive = true;
		ClearSuspendHold(unlockLogReason);
		KuroLog.SystemEvent("session unlock reason=" + unlockLogReason, "DispatchActive", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Input\\SystemAvailabilityEvents.cs", 522);
		InvokeEach(SessionBecameActiveGate, reason, "SessionBecameActiveGate");
		InvokeEach(SessionBecameActive, reason, "SessionBecameActive");
	}

	private static void InvokeEach(Action? handlers, string eventName)
	{
		if (handlers == null)
		{
			return;
		}
		Delegate[] invocationList = handlers.GetInvocationList();
		foreach (Delegate obj in invocationList)
		{
			try
			{
				((Action)obj)();
			}
			catch (Exception value)
			{
				KuroLog.SystemEvent($"SystemAvailabilityEvents {eventName} subscriber failed: {value}", "InvokeEach", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Input\\SystemAvailabilityEvents.cs", 541);
			}
		}
	}

	private static void InvokeEach<T>(Action<T>? handlers, T arg, string eventName)
	{
		if (handlers == null)
		{
			return;
		}
		Delegate[] invocationList = handlers.GetInvocationList();
		foreach (Delegate obj in invocationList)
		{
			try
			{
				((Action<T>)obj)(arg);
			}
			catch (Exception value)
			{
				KuroLog.SystemEvent($"SystemAvailabilityEvents {eventName} subscriber failed: {value}", "InvokeEach", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Input\\SystemAvailabilityEvents.cs", 560);
			}
		}
	}

	private void PowerThreadProc()
	{
		_powerThreadId = GetCurrentThreadId();
		PowerWindow powerWindow = new PowerWindow(this);
		powerWindow.Create();
		_powerNotifHandle = RegisterSuspendResumeNotification(powerWindow.Handle, 0u);
		if (_powerNotifHandle == IntPtr.Zero)
		{
			KuroLog.SystemEvent("IdleManager suspend notification registration returned no handle; fallback=session_switch_events_only", "PowerThreadProc", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Input\\SystemAvailabilityEvents.cs", 576);
		}
		int message;
		MSG msg;
		while ((message = GetMessage(out msg, IntPtr.Zero, 0u, 0u)) > 0)
		{
			TranslateMessage(ref msg);
			DispatchMessage(ref msg);
		}
		if (message == -1)
		{
			KuroLog.SystemEvent($"IdleManager power thread message loop ended with error win32={Marshal.GetLastWin32Error()}", "PowerThreadProc", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Input\\SystemAvailabilityEvents.cs", 590);
		}
		if (_powerNotifHandle != IntPtr.Zero)
		{
			UnregisterSuspendResumeNotification(_powerNotifHandle);
		}
		powerWindow.DestroyHandle();
	}

	internal void EnqueueSignalForTests(SystemAvailabilitySignal signal)
	{
		_signals.Writer.TryWrite(signal);
	}

	public void Dispose()
	{
		if (Interlocked.Exchange(ref _disposed, 1) != 0)
		{
			return;
		}
		if (_attachedToOsSources)
		{
			SystemEvents.SessionSwitch -= OnSessionSwitch;
		}
		if (_powerThreadId != 0)
		{
			PostThreadMessage(_powerThreadId, 18u, IntPtr.Zero, IntPtr.Zero);
		}
		_powerThread?.Join(2000);
		_powerThread = null;
		_signals.Writer.TryComplete();
		try
		{
			_consumer.Wait(2000);
		}
		catch
		{
		}
	}
}
