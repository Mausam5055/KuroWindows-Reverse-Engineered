using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using KuroWindows.RecorderCore.Clock;

namespace KuroWindows.Input.Touchpad;

internal sealed class TouchpadChannel : IDisposable
{
	private struct POINT
	{
		public int x;

		public int y;
	}

	private struct MSG
	{
		public nint hwnd;

		public uint message;

		public nint wParam;

		public nint lParam;

		public uint time;

		public POINT pt;
	}

	private sealed class PtpDevice : IPtpGestureSink, IDisposable
	{
		private readonly TouchpadChannel _channel;

		private readonly HidInterop.HIDP_DATA[] _data;

		private readonly PtpSlot[] _slots;

		private long _rateWindowStartQpc;

		private int _rateFrames;

		private bool _reportModeDecided;

		private bool _hybridBroken;

		public int Index { get; }

		public PtpDevicePlan Plan { get; }

		public PtpFrameAssembler Assembler { get; }

		public PtpGestureMachine Machine { get; }

		public TouchpadDeviceRecord Record { get; set; }

		public (int X, int Y, int Screen) Pointer { get; set; }

		public uint LastOsTime { get; set; }

		public PtpFrame Pending { get; } = new PtpFrame();

		public int PendingCount { get; set; }

		public long PendingStart { get; set; }

		public PtpDevice(TouchpadChannel channel, int index, PtpDevicePlan plan, TouchpadSettingsSnapshot settings, int touchscreens, int pens, string arch, bool registered, int registerError)
		{
			PtpDevice device = this;
			_channel = channel;
			Index = index;
			Plan = plan;
			_data = new HidInterop.HIDP_DATA[Math.Max(1u, plan.MaxDataListLength)];
			_slots = new PtpSlot[Math.Max(1, plan.Slots)];
			Assembler = new PtpFrameAssembler(plan, channel._counters, (PtpFrame frame) =>
			{
				channel.OnFrame(device, frame);
			});
			Machine = new PtpGestureMachine(plan, this, () => Interlocked.Increment(ref channel._gestureCounter));
			Record = TouchpadDeviceRecord.Of(index, plan, settings, touchscreens, pens, arch, registered, registerError);
		}

		public void Decode(nint report, int length, long qpc, uint osTime)
		{
			uint dataLength = (uint)_data.Length;
			if (HidInterop.HidP_GetData(0, _data, ref dataLength, Plan.Preparsed, report, (uint)length) != 1114112)
			{
				Interlocked.Increment(ref _channel._counters.RawFail);
				return;
			}
			Array.Clear(_slots);
			int num = ((!Plan.HasContactCount) ? (-1) : 0);
			int scanTime = -1;
			int num2 = 0;
			PtpDataIndex[] table = Plan.Table;
			for (int i = 0; i < dataLength; i++)
			{
				HidInterop.HIDP_DATA hIDP_DATA = _data[i];
				if (hIDP_DATA.DataIndex >= table.Length)
				{
					continue;
				}
				PtpDataIndex ptpDataIndex = table[hIDP_DATA.DataIndex];
				if (ptpDataIndex.Slot != byte.MaxValue)
				{
					if (ptpDataIndex.Slot < _slots.Length)
					{
						ref PtpSlot reference = ref _slots[ptpDataIndex.Slot];
						switch (ptpDataIndex.Field)
						{
						case PtpField.ContactId:
							reference.Id = (int)hIDP_DATA.RawValue;
							break;
						case PtpField.X:
							reference.X = (int)hIDP_DATA.RawValue;
							break;
						case PtpField.Y:
							reference.Y = (int)hIDP_DATA.RawValue;
							break;
						case PtpField.Tip:
							reference.Tip = hIDP_DATA.On;
							break;
						case PtpField.Confidence:
							reference.Conf = hIDP_DATA.On;
							break;
						}
					}
					continue;
				}
				switch (ptpDataIndex.Field)
				{
				case PtpField.ScanTime:
					scanTime = (int)hIDP_DATA.RawValue;
					break;
				case PtpField.ContactCount:
					num = (int)hIDP_DATA.RawValue;
					break;
				case PtpField.Button1:
					if (hIDP_DATA.On)
					{
						num2 |= 1;
					}
					break;
				case PtpField.Button2:
					if (hIDP_DATA.On)
					{
						num2 |= 2;
					}
					break;
				case PtpField.Button3:
					if (hIDP_DATA.On)
					{
						num2 |= 4;
					}
					break;
				}
			}
			if (num < 0)
			{
				num = 0;
				for (int j = 0; j < _slots.Length; j++)
				{
					if (_slots[j].Tip)
					{
						num++;
					}
				}
			}
			Assembler.OnReport(_slots, _slots.Length, num, scanTime, num2, qpc, osTime);
		}

		public void NoteFrame(PtpFrame frame, PtpCounters counters)
		{
			bool flag = false;
			if (Assembler.ConfidenceDecided)
			{
				Assembler.ConfidenceDecided = false;
				Record = Record with
				{
					ConfidenceUsable = Assembler.ConfidenceUsable
				};
				flag = true;
			}
			object obj;
			if (!_reportModeDecided && !frame.Partial && !frame.Synthetic && frame.ContactCount > 0)
			{
				if (frame.Reports <= 1)
				{
					int? maxContacts = Record.MaxContacts;
					if (maxContacts.HasValue)
					{
						int valueOrDefault = maxContacts.GetValueOrDefault();
						if (Plan.Slots >= valueOrDefault)
						{
							obj = "parallel";
							goto IL_00a6;
						}
					}
					obj = null;
				}
				else
				{
					obj = "hybrid";
				}
				goto IL_00a6;
			}
			goto IL_00cb;
			IL_00a6:
			string text = (string)obj;
			if (text != null)
			{
				_reportModeDecided = true;
				Record = Record with
				{
					ReportMode = text
				};
				flag = true;
			}
			goto IL_00cb;
			IL_00cb:
			if (!_hybridBroken)
			{
				long num = Volatile.Read(in counters.Frames);
				if (num >= 100 && Volatile.Read(in counters.Partial) * 10 > num)
				{
					_hybridBroken = true;
					Record = Record with
					{
						ReportMode = "hybrid_broken"
					};
					flag = true;
				}
			}
			if (!Record.RateHz.HasValue)
			{
				if (frame.Fingers == 0 || frame.Synthetic)
				{
					_rateWindowStartQpc = 0L;
					_rateFrames = 0;
				}
				else if (_rateWindowStartQpc == 0L)
				{
					_rateWindowStartQpc = frame.Qpc;
					_rateFrames = 1;
				}
				else
				{
					_rateFrames++;
					if (RecordingClock.MsBetween(_rateWindowStartQpc, frame.Qpc) >= 1000)
					{
						Record = Record with
						{
							RateHz = _rateFrames
						};
						flag = true;
					}
				}
			}
			if (flag)
			{
				_channel.PublishRecords();
				_channel.Log(Record.ToRow("change", frame.WallMs, includePreparsed: false), frame.WallMs);
			}
		}

		public void FlushPending()
		{
			if (PendingCount != 0)
			{
				int pendingCount = PendingCount;
				PendingCount = 0;
				_channel.WriteTouchRow(this, Pending, pendingCount);
			}
		}

		public void ScrollBegan(int gestureId, double dxPx, double dyPx, long t)
		{
			_channel.WriteScroll(this, gestureId, dxPx, dyPx, 1, t, RowFlags.None);
			_channel._frameState?.AppendScroll(dxPx, dyPx, Pointer.Screen, t, "t");
			_channel._onUserInteraction(InputKind.Pointer);
		}

		public void ScrollChanged(int gestureId, double dxPx, double dyPx, long t)
		{
			_channel.WriteScroll(this, gestureId, dxPx, dyPx, 4, t, RowFlags.None);
			_channel._frameState?.AppendScroll(dxPx, dyPx, Pointer.Screen, t, "t");
		}

		public void ScrollEnded(int gestureId, double velocityX, double velocityY, double totalMmX, double totalMmY, ScrollEndReason reason, long t)
		{
			_channel.WriteScroll(this, gestureId, 0.0, 0.0, 8, t, RowFlags.Ended, velocityX, velocityY, totalMmX, totalMmY, reason);
			Volatile.Write(ref _channel._lastScrollEndQpc, Stopwatch.GetTimestamp());
			Interlocked.Increment(ref _channel._counters.GestureScroll);
			_channel._onUserInteraction(InputKind.Pointer);
		}

		public void GestureRow(string kind, int fingers, double? scale, double dxMm, double dyMm, long durationMs, int gestureId, long t)
		{
			_channel.Log(new
			{
				type = "gesture",
				kind = kind,
				fingers = fingers,
				scale = scale,
				dxMm = dxMm,
				dyMm = dyMm,
				durationMs = durationMs,
				gestureId = gestureId,
				timestamp = t
			}, t);
			if (!(kind == "pinch"))
			{
				if (kind == "swipe")
				{
					Interlocked.Increment(ref _channel._counters.GestureSwipe);
				}
				else
				{
					Interlocked.Increment(ref _channel._counters.GesturePan);
				}
			}
			else
			{
				Interlocked.Increment(ref _channel._counters.GesturePinch);
			}
		}

		public void GestureWithoutRow()
		{
			Interlocked.Increment(ref _channel._counters.GestureOther);
		}

		public void Dispose()
		{
			Plan.Dispose();
		}
	}

	private sealed class TouchWindow : NativeWindow
	{
		private readonly TouchpadChannel _channel;

		internal TouchWindow(TouchpadChannel channel)
		{
			_channel = channel;
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
			try
			{
				switch (m.Msg)
				{
				case 255:
					_channel.OnRawInput(m.LParam);
					break;
				case 254:
					_channel.OnDeviceChange((int)m.WParam, m.LParam);
					break;
				case 275:
					switch ((int)m.WParam)
					{
					case 1:
						_channel.OnLiftTimer();
						break;
					case 2:
						_channel.CheckRegistration(Handle);
						break;
					}
					break;
				case 32784:
					_channel.OnSettings((int)m.WParam);
					break;
				case 32786:
					_channel.OnEndGestures();
					break;
				case 32785:
					_channel.OnStop();
					break;
				}
			}
			catch (Exception arg)
			{
				_channel._noteCallbackFailure("touch_input", arg);
			}
			base.WndProc(ref m);
		}
	}

	private const int WM_TIMER = 275;

	private const uint WM_APP = 32768u;

	private const uint WM_TOUCH_SETTINGS = 32784u;

	private const uint WM_TOUCH_STOP = 32785u;

	private const uint WM_TOUCH_END_GESTURES = 32786u;

	private const uint SMTO_ABORTIFHUNG = 2u;

	private const uint EndGesturesTimeoutMs = 1000u;

	private const int LiftTimerId = 1;

	private const int RegistrationTimerId = 2;

	private const uint RegistrationCheckMs = 30000u;

	private const long TouchBudgetBytes = 8388608L;

	private const int TouchRowBaseBytes = 150;

	private const int TouchRowContactBytes = 19;

	private const int TouchRowFramesBytes = 12;

	private const int MaxForeignHandles = 64;

	private const long RateWindowMs = 1000L;

	private const long HybridBrokenMinFrames = 100L;

	private static readonly string[] SettingsReasons = new string[3] { "user_preference", "touchpad_registry", "resume" };

	private readonly Func<IEventSink?> _sink;

	private readonly FrameStateSource? _frameState;

	private readonly Func<AppContext> _foreground;

	private readonly Func<MousePositionEntry?> _lastPointer;

	private readonly Func<uint> _modifiers;

	private readonly Action<InputKind> _onUserInteraction;

	private readonly Action<string, Exception> _noteCallbackFailure;

	private Thread? _thread;

	private nint _windowHandle;

	private readonly ManualResetEventSlim _windowReady = new ManualResetEventSlim(initialState: false);

	private nint _buffer;

	private uint _bufferSize;

	private readonly Dictionary<nint, PtpDevice> _devices = new Dictionary<nint, PtpDevice>();

	private readonly HashSet<nint> _gamepads = new HashSet<nint>();

	private readonly HashSet<nint> _ignored = new HashSet<nint>();

	private int _nextDeviceIndex;

	private int _touchscreens;

	private int _pens;

	private readonly string _arch = RuntimeInformation.ProcessArchitecture.ToString();

	private readonly PtpCounters _counters = new PtpCounters();

	private TouchpadSettings.RegistryWatch? _registryWatch;

	private int _registered;

	private int _registerError;

	private bool _registrationFailedLogged;

	private bool _liftTimerArmed;

	private int _gestureCounter;

	private long _touchBytes;

	private int _budgetTripped;

	private TouchpadDeviceRecord[] _records = Array.Empty<TouchpadDeviceRecord>();

	private InputSettingsSnapshot? _settings;

	private int _gestureState;

	private long _lastScrollFrameQpc;

	private long _lastScrollEndQpc;

	private int _currentGestureId;

	private long _lastReportQpc;

	public IReadOnlyList<TouchpadDeviceRecord> Devices => Volatile.Read(in _records);

	public InputSettingsSnapshot? Settings => Volatile.Read(in _settings);

	public bool Registered => Volatile.Read(in _registered) != 0;

	public int CurrentGestureId => Volatile.Read(in _currentGestureId);

	public long LastReportQpc => Volatile.Read(in _lastReportQpc);

	[DllImport("user32.dll")]
	private static extern int GetMessage(out MSG msg, nint hwnd, uint min, uint max);

	[DllImport("user32.dll")]
	private static extern bool TranslateMessage(ref MSG msg);

	[DllImport("user32.dll")]
	private static extern nint DispatchMessage(ref MSG msg);

	[DllImport("user32.dll")]
	private static extern bool PostMessage(nint hwnd, uint msg, nint w, nint l);

	[DllImport("user32.dll")]
	private static extern nint SendMessageTimeout(nint hwnd, uint msg, nint w, nint l, uint flags, uint timeoutMs, out nint result);

	[DllImport("user32.dll")]
	private static extern void PostQuitMessage(int code);

	[DllImport("user32.dll")]
	private static extern int GetMessageTime();

	[DllImport("user32.dll")]
	private static extern nuint SetTimer(nint hwnd, nuint id, uint elapseMs, nint proc);

	[DllImport("user32.dll")]
	private static extern bool KillTimer(nint hwnd, nuint id);

	public TouchpadChannel(Func<IEventSink?> sink, FrameStateSource? frameState, Func<AppContext> foreground, Func<MousePositionEntry?> lastPointer, Func<uint> modifiers, Action<InputKind> onUserInteraction, Action<string, Exception> noteCallbackFailure)
	{
		_sink = sink;
		_frameState = frameState;
		_foreground = foreground;
		_lastPointer = lastPointer;
		_modifiers = modifiers;
		_onUserInteraction = onUserInteraction;
		_noteCallbackFailure = noteCallbackFailure;
	}

	public bool WaitForRegistration(int timeoutMs)
	{
		return _windowReady.Wait(timeoutMs);
	}

	public void Start()
	{
		InputSettingsSnapshot value = InputSettingsSnapshot.Read();
		Volatile.Write(ref _settings, value);
		try
		{
			uint num = 0u;
			uint size = (uint)Marshal.SizeOf<HidInterop.RAWINPUTDEVICELIST>();
			if (HidInterop.GetRawInputDeviceList(null, ref num, size) != uint.MaxValue && num != 0)
			{
				HidInterop.RAWINPUTDEVICELIST[] array = new HidInterop.RAWINPUTDEVICELIST[num];
				uint rawInputDeviceList = HidInterop.GetRawInputDeviceList(array, ref num, size);
				if (rawInputDeviceList != uint.MaxValue)
				{
					for (int i = 0; i < Math.Min(rawInputDeviceList, (uint)array.Length); i++)
					{
						if (array[i].dwType == 2 && HidInterop.TryDeviceInfo(array[i].hDevice, out var info))
						{
							if (info.UsagePage == 13 && info.Usage == 4)
							{
								_touchscreens++;
							}
							else if (info.UsagePage == 13 && info.Usage == 2)
							{
								_pens++;
							}
							else
							{
								Classify(array[i].hDevice, in info, null);
							}
						}
					}
				}
			}
		}
		catch (Exception arg)
		{
			_noteCallbackFailure("touch_enumerate", arg);
		}
		PublishRecords();
		try
		{
			_registryWatch = new TouchpadSettings.RegistryWatch(() =>
			{
				RequestSettingsReread("touchpad_registry");
			}, (Exception ex) =>
			{
				_noteCallbackFailure("touch_settings_watch", ex);
			});
		}
		catch (Exception arg2)
		{
			_noteCallbackFailure("touch_settings_watch", arg2);
		}
		_thread = new Thread(ThreadProc)
		{
			IsBackground = true,
			Name = "KuroTouch"
		};
		_thread.Start();
	}

	public void Stop()
	{
		Thread thread = Interlocked.Exchange(ref _thread, null);
		if (thread != null)
		{
			Interlocked.Exchange(ref _registryWatch, null)?.Dispose();
			_windowReady.Wait(1000);
			nint num = Volatile.Read(in _windowHandle);
			if (num != IntPtr.Zero)
			{
				PostMessage(num, 32785u, IntPtr.Zero, IntPtr.Zero);
			}
			if (!thread.Join(2000))
			{
				KuroLog.SystemEvent("touch thread join timed out after 2000ms; thread may still be running", "Stop", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Input\\Touchpad\\TouchpadChannel.cs", 215);
			}
		}
	}

	public void Dispose()
	{
		Stop();
		_windowReady.Dispose();
	}

	public void EndGestures()
	{
		nint num = Volatile.Read(in _windowHandle);
		if (num != IntPtr.Zero)
		{
			SendMessageTimeout(num, 32786u, IntPtr.Zero, IntPtr.Zero, 2u, 1000u, out var _);
		}
	}

	public void RequestSettingsReread(string reason)
	{
		int num = Array.IndexOf(SettingsReasons, reason);
		nint num2 = Volatile.Read(in _windowHandle);
		if (num >= 0 && num2 != IntPtr.Zero)
		{
			PostMessage(num2, 32784u, new IntPtr(num), IntPtr.Zero);
		}
	}

	public void NoteOrphanWheel()
	{
		Interlocked.Increment(ref _counters.OrphanWheels);
	}

	public bool IsOsCopy(long nowQpc, ref long lastOsWheelQpc)
	{
		long num = Stopwatch.Frequency / 10;
		int num2;
		if (Volatile.Read(in _gestureState) != 2 && nowQpc - Volatile.Read(in _lastScrollFrameQpc) > num)
		{
			if (nowQpc - lastOsWheelQpc <= num)
			{
				num2 = ((nowQpc - Volatile.Read(in _lastScrollEndQpc) <= 3 * Stopwatch.Frequency) ? 1 : 0);
				if (num2 != 0)
				{
					goto IL_0052;
				}
			}
			else
			{
				num2 = 0;
			}
			goto IL_0066;
		}
		num2 = 1;
		goto IL_0052;
		IL_0066:
		return (byte)num2 != 0;
		IL_0052:
		lastOsWheelQpc = nowQpc;
		Interlocked.Increment(ref _counters.OsRows);
		goto IL_0066;
	}

	public TouchChannelFacts SnapshotAndReset()
	{
		int num = 0;
		TouchpadDeviceRecord[] array = Volatile.Read(in _records);
		for (int i = 0; i < array.Length; i++)
		{
			if (array[i].Present)
			{
				num++;
			}
		}
		Interlocked.Exchange(ref _touchBytes, 0L);
		Interlocked.Exchange(ref _budgetTripped, 0);
		PtpCounters counters = _counters;
		return new TouchChannelFacts(new TouchpadFactsSnapshot(num, Interlocked.Exchange(ref counters.Frames, 0L), Interlocked.Exchange(ref counters.Reports, 0L), Interlocked.Exchange(ref counters.Partial, 0L), Interlocked.Exchange(ref counters.Timeouts, 0L), Interlocked.Exchange(ref counters.DupIds, 0L), Interlocked.Exchange(ref counters.UnknownReportId, 0L), Interlocked.Exchange(ref counters.UnknownDevice, 0L), Interlocked.Exchange(ref counters.LenMismatch, 0L), Interlocked.Exchange(ref counters.RawFail, 0L), Interlocked.Exchange(ref counters.BufferGrow, 0L), Interlocked.Exchange(ref counters.ContactsTruncated, 0L), Interlocked.Exchange(ref counters.GestureScroll, 0L), Interlocked.Exchange(ref counters.GesturePinch, 0L), Interlocked.Exchange(ref counters.GestureSwipe, 0L), Interlocked.Exchange(ref counters.GesturePan, 0L), Interlocked.Exchange(ref counters.GestureOther, 0L), Interlocked.Exchange(ref counters.ScrollRows, 0L), Interlocked.Exchange(ref counters.OsRows, 0L), Interlocked.Exchange(ref counters.OrphanWheels, 0L), Interlocked.Exchange(ref counters.PalmContacts, 0L), Registered, (int)Interlocked.Exchange(ref counters.RegisterFail, 0L), (int)Interlocked.Exchange(ref counters.Reregisters, 0L)), Interlocked.Exchange(ref counters.GamepadPackets, 0L));
	}

	private void ThreadProc()
	{
		TouchWindow touchWindow = null;
		try
		{
			_bufferSize = 1024u;
			_buffer = Marshal.AllocHGlobal(1024);
			touchWindow = new TouchWindow(this);
			touchWindow.Create();
			Volatile.Write(ref _windowHandle, touchWindow.Handle);
			Register(touchWindow.Handle, reregistration: false);
			SetTimer(touchWindow.Handle, 2u, 30000u, IntPtr.Zero);
		}
		catch (Exception arg)
		{
			_noteCallbackFailure("touch_thread", arg);
		}
		finally
		{
			_windowReady.Set();
		}
		if (touchWindow != null && touchWindow.Handle != IntPtr.Zero)
		{
			MSG msg;
			while (GetMessage(out msg, IntPtr.Zero, 0u, 0u) > 0)
			{
				TranslateMessage(ref msg);
				DispatchMessage(ref msg);
			}
			KillTimer(touchWindow.Handle, 1u);
			KillTimer(touchWindow.Handle, 2u);
			HidInterop.RegisterRawInputDevices(new HidInterop.RAWINPUTDEVICE[3]
			{
				new HidInterop.RAWINPUTDEVICE
				{
					usUsagePage = 13,
					usUsage = 5,
					dwFlags = 1u
				},
				new HidInterop.RAWINPUTDEVICE
				{
					usUsagePage = 1,
					usUsage = 4,
					dwFlags = 1u
				},
				new HidInterop.RAWINPUTDEVICE
				{
					usUsagePage = 1,
					usUsage = 5,
					dwFlags = 1u
				}
			}, 3u, (uint)Marshal.SizeOf<HidInterop.RAWINPUTDEVICE>());
			Volatile.Write(ref _windowHandle, IntPtr.Zero);
			touchWindow.DestroyHandle();
		}
		Marshal.FreeHGlobal(_buffer);
		_buffer = IntPtr.Zero;
		foreach (PtpDevice value in _devices.Values)
		{
			value.Dispose();
		}
		_devices.Clear();
	}

	private void Register(nint window, bool reregistration)
	{
		uint size = (uint)Marshal.SizeOf<HidInterop.RAWINPUTDEVICE>();
		bool flag = HidInterop.RegisterRawInputDevices(new HidInterop.RAWINPUTDEVICE[1]
		{
			new HidInterop.RAWINPUTDEVICE
			{
				usUsagePage = 13,
				usUsage = 5,
				dwFlags = 8448u,
				hwndTarget = window
			}
		}, 1u, size);
		int num = ((!flag) ? Marshal.GetLastWin32Error() : 0);
		HidInterop.RegisterRawInputDevices(new HidInterop.RAWINPUTDEVICE[2]
		{
			new HidInterop.RAWINPUTDEVICE
			{
				usUsagePage = 1,
				usUsage = 4,
				dwFlags = 256u,
				hwndTarget = window
			},
			new HidInterop.RAWINPUTDEVICE
			{
				usUsagePage = 1,
				usUsage = 5,
				dwFlags = 256u,
				hwndTarget = window
			}
		}, 2u, size);
		_registerError = num;
		Volatile.Write(ref _registered, flag ? 1 : 0);
		if (flag)
		{
			if (reregistration)
			{
				Interlocked.Increment(ref _counters.Reregisters);
			}
		}
		else
		{
			Interlocked.Increment(ref _counters.RegisterFail);
			if (!_registrationFailedLogged)
			{
				_registrationFailedLogged = true;
				KuroLog.SystemEvent($"touchpad registration failed err={num}", "Register", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Input\\Touchpad\\TouchpadChannel.cs", 405);
			}
		}
		foreach (PtpDevice value in _devices.Values)
		{
			value.Record = value.Record with
			{
				Registered = flag,
				RegisterError = num
			};
		}
		PublishRecords();
	}

	private void CheckRegistration(nint window)
	{
		uint num = 0u;
		uint size = (uint)Marshal.SizeOf<HidInterop.RAWINPUTDEVICE>();
		bool flag = false;
		HidInterop.GetRegisteredRawInputDevices(null, ref num, size);
		if (num != 0)
		{
			HidInterop.RAWINPUTDEVICE[] array = new HidInterop.RAWINPUTDEVICE[num];
			uint registeredRawInputDevices = HidInterop.GetRegisteredRawInputDevices(array, ref num, size);
			if (registeredRawInputDevices != uint.MaxValue)
			{
				for (int i = 0; i < Math.Min(registeredRawInputDevices, (uint)array.Length); i++)
				{
					HidInterop.RAWINPUTDEVICE rAWINPUTDEVICE = array[i];
					if (rAWINPUTDEVICE.usUsagePage == 13 && rAWINPUTDEVICE.usUsage == 5 && rAWINPUTDEVICE.hwndTarget == window)
					{
						flag = true;
						break;
					}
				}
			}
		}
		if (!flag)
		{
			Register(window, reregistration: true);
		}
	}

	private void PublishRecords()
	{
		TouchpadDeviceRecord[] value;
		if (_devices.Count == 0)
		{
			InputSettingsSnapshot inputSettingsSnapshot = Volatile.Read(in _settings);
			value = new TouchpadDeviceRecord[1] { TouchpadDeviceRecord.Absent(_touchscreens, _pens, inputSettingsSnapshot?.Touchpad.LegacyPresent == true, _arch) };
		}
		else
		{
			value = (from d in _devices.Values
				orderby d.Index
				select d.Record).ToArray();
		}
		Volatile.Write(ref _records, value);
	}

	private PtpDevice? Classify(nint hDevice, in HidInterop.RidDeviceInfo info, string? announce)
	{
		if (info.Type == 2 && info.UsagePage == 13 && info.Usage == 5)
		{
			PtpDevicePlan ptpDevicePlan = PtpDevicePlan.TryBuild(hDevice, in info);
			if (ptpDevicePlan == null)
			{
				Remember(_ignored, hDevice);
				return null;
			}
			InputSettingsSnapshot inputSettingsSnapshot = Volatile.Read(in _settings);
			PtpDevice ptpDevice = new PtpDevice(this, _nextDeviceIndex++, ptpDevicePlan, inputSettingsSnapshot.Touchpad, _touchscreens, _pens, _arch, Registered, _registerError);
			_devices[hDevice] = ptpDevice;
			PublishRecords();
			if (announce != null)
			{
				long num = NowMs();
				Log(ptpDevice.Record.ToRow(announce, num, includePreparsed: false), num);
			}
			return ptpDevice;
		}
		bool flag = info.Type == 2 && info.UsagePage == 1;
		if (flag)
		{
			ushort usage = info.Usage;
			bool flag2 = (uint)(usage - 4) <= 1u;
			flag = flag2;
		}
		if (flag)
		{
			Remember(_gamepads, hDevice);
			return null;
		}
		Remember(_ignored, hDevice);
		return null;
	}

	private static void Remember(HashSet<nint> handles, nint handle)
	{
		if (handles.Count >= 64)
		{
			handles.Clear();
		}
		handles.Add(handle);
	}

	private void OnDeviceChange(int change, nint hDevice)
	{
		switch (change)
		{
		case 1:
		{
			if (!_devices.ContainsKey(hDevice) && !_gamepads.Contains(hDevice) && HidInterop.TryDeviceInfo(hDevice, out var info))
			{
				_ignored.Remove(hDevice);
				Classify(hDevice, in info, "arrival");
			}
			break;
		}
		case 2:
		{
			_gamepads.Remove(hDevice);
			_ignored.Remove(hDevice);
			if (_devices.Remove(hDevice, out PtpDevice value))
			{
				long num = NowMs();
				value.Machine.End(num, ScrollEndReason.DeviceRemoved);
				PublishGesture(value, 0L);
				value.FlushPending();
				Log(value.Record.ToRow("removal", num, includePreparsed: false), num);
				value.Dispose();
				PublishRecords();
			}
			break;
		}
		}
	}

	private void OnRawInput(nint hRawInput)
	{
		long timestamp = Stopwatch.GetTimestamp();
		uint messageTime = (uint)GetMessageTime();
		bool flag = HidInterop.TryReadRawInput(hRawInput, ref _buffer, ref _bufferSize, out var growths);
		if (growths > 0)
		{
			Interlocked.Add(ref _counters.BufferGrow, growths);
		}
		if (!flag)
		{
			Interlocked.Increment(ref _counters.RawFail);
			return;
		}
		HidInterop.RAWINPUTHEADER rAWINPUTHEADER = Marshal.PtrToStructure<HidInterop.RAWINPUTHEADER>(_buffer);
		if (rAWINPUTHEADER.dwType != 2)
		{
			return;
		}
		if (_gamepads.Contains(rAWINPUTHEADER.hDevice))
		{
			Interlocked.Increment(ref _counters.GamepadPackets);
			return;
		}
		if (!_devices.TryGetValue(rAWINPUTHEADER.hDevice, out PtpDevice value))
		{
			if (_ignored.Contains(rAWINPUTHEADER.hDevice) || !HidInterop.TryDeviceInfo(rAWINPUTHEADER.hDevice, out var info))
			{
				Interlocked.Increment(ref _counters.UnknownDevice);
				return;
			}
			value = Classify(rAWINPUTHEADER.hDevice, in info, "arrival");
			if (value == null)
			{
				Interlocked.Increment(ref _gamepads.Contains(rAWINPUTHEADER.hDevice) ? ref _counters.GamepadPackets : ref _counters.UnknownDevice);
				return;
			}
		}
		Volatile.Write(ref _lastReportQpc, timestamp);
		int num = Marshal.ReadInt32(_buffer, 24);
		int num2 = Marshal.ReadInt32(_buffer, 28);
		if (num <= 0 || num2 <= 0 || 32 + (long)num * (long)num2 > rAWINPUTHEADER.dwSize)
		{
			Interlocked.Increment(ref _counters.RawFail);
			return;
		}
		Interlocked.Add(ref _counters.Reports, num2);
		if (num != value.Plan.ReportBytes)
		{
			Interlocked.Add(ref _counters.LenMismatch, num2);
			return;
		}
		for (int i = 0; i < num2; i++)
		{
			nint num3 = _buffer + 32 + i * num;
			if (!value.Plan.ReportIds.Contains(Marshal.ReadByte(num3)))
			{
				Interlocked.Increment(ref _counters.UnknownReportId);
			}
			else
			{
				value.Decode(num3, num, timestamp, messageTime);
			}
		}
	}

	private void OnFrame(PtpDevice device, PtpFrame frame)
	{
		TouchpadSettingsSnapshot touchpadSettingsSnapshot = Volatile.Read(in _settings)?.Touchpad;
		MousePositionEntry mousePositionEntry = _lastPointer();
		int num;
		int item;
		int item2;
		if (mousePositionEntry != null)
		{
			int x = mousePositionEntry.X;
			int y = mousePositionEntry.Y;
			num = mousePositionEntry.Screen;
			item = y;
			item2 = x;
		}
		else
		{
			Point position = Cursor.Position;
			int x2 = position.X;
			int y2 = position.Y;
			num = MonitorTable.Current.IndexOfNearest(position.X, position.Y);
			item = y2;
			item2 = x2;
		}
		IReadOnlyList<MonitorTable.Entry> screens = MonitorTable.Current.Screens;
		uint num2 = ((num >= 0 && num < screens.Count) ? screens[num].Dpi : 96u);
		device.Pointer = (X: item2, Y: item, Screen: num);
		device.LastOsTime = frame.OsTime;
		double gainPxPerMm = 1.0 * (double)num2 / 25.4;
		int sign = (((object)touchpadSettingsSnapshot == null || !touchpadSettingsSnapshot.Reversed) ? 1 : (-1));
		bool scrollRows = touchpadSettingsSnapshot == null || (touchpadSettingsSnapshot.Pan && touchpadSettingsSnapshot.Active != false);
		device.Machine.OnFrame(frame, gainPxPerMm, sign, scrollRows, (!frame.Synthetic) ? ScrollEndReason.Lift : ScrollEndReason.Timeout);
		PublishGesture(device, frame.Qpc);
		if (frame.Fingers > 0)
		{
			_onUserInteraction(InputKind.Touch);
		}
		EmitTouch(device, frame);
		device.NoteFrame(frame, _counters);
		nint hwnd = Volatile.Read(in _windowHandle);
		if (device.Assembler.ContactsDown)
		{
			SetTimer(hwnd, 1u, 100u, IntPtr.Zero);
			_liftTimerArmed = true;
		}
		else if (_liftTimerArmed && !AnyContactsDown())
		{
			KillTimer(hwnd, 1u);
			_liftTimerArmed = false;
		}
	}

	private bool AnyContactsDown()
	{
		foreach (PtpDevice value in _devices.Values)
		{
			if (value.Assembler.ContactsDown)
			{
				return true;
			}
		}
		return false;
	}

	private void PublishGesture(PtpDevice device, long frameQpc)
	{
		PtpGestureMachine.GestureState state = device.Machine.State;
		Volatile.Write(ref _gestureState, (int)state);
		if (state == PtpGestureMachine.GestureState.Scroll)
		{
			Volatile.Write(ref _currentGestureId, device.Machine.GestureId);
			if (frameQpc != 0L)
			{
				Volatile.Write(ref _lastScrollFrameQpc, frameQpc);
			}
		}
	}

	private void EmitTouch(PtpDevice device, PtpFrame frame)
	{
		bool flag = Volatile.Read(in _budgetTripped) != 0;
		if (frame.Transition || (!flag && (frame.Fingers >= 2 || frame.AnyPalm)))
		{
			device.FlushPending();
			WriteTouchRow(device, frame, 0);
		}
		else if (frame.Count != 0)
		{
			if (device.PendingCount == 0)
			{
				device.PendingStart = frame.WallMs;
			}
			device.Pending.CopyFrom(frame);
			device.PendingCount++;
			if (frame.WallMs - device.PendingStart >= 40)
			{
				device.FlushPending();
			}
		}
	}

	private void WriteTouchRow(PtpDevice device, PtpFrame frame, int frames)
	{
		int num = Math.Min(frame.Count, 8);
		if (frame.Count > num)
		{
			Interlocked.Add(ref _counters.ContactsTruncated, frame.Count - num);
		}
		TouchContacts contacts = default;
		for (int i = 0; i < num; i++)
		{
			PtpContact ptpContact = frame.Contacts[i];
			contacts[i] = new TouchContact
			{
				Id = ptpContact.Id,
				X = ptpContact.X,
				Y = ptpContact.Y,
				Flags = (byte)((ptpContact.Tip ? 1u : 0u) | (uint)(ptpContact.Conf ? 2 : 0))
			};
		}
		Enqueue(new EventRow
		{
			Kind = RowKind.Touch,
			Flags = ((frames > 0) ? RowFlags.Coalesced : RowFlags.None),
			WallMs = frame.WallMs,
			OsTime = frame.OsTime,
			Device = device.Index,
			ScanTime = frame.ScanTimeRaw,
			ContactCount = frame.ContactCount,
			Buttons = frame.Buttons,
			Frames = frames,
			ContactsCarried = num,
			Contacts = contacts
		});
		int num2 = 150 + 19 * num + ((frames > 0) ? 12 : 0);
		long num3 = Interlocked.Add(ref _touchBytes, num2);
		if (num3 >= 8388608 && Interlocked.Exchange(ref _budgetTripped, 1) == 0)
		{
			Log(new
			{
				type = "touch_budget",
				bytes = num3,
				timestamp = frame.WallMs
			}, frame.WallMs);
		}
	}

	private void WriteScroll(PtpDevice device, int gestureId, double dx, double dy, int phase, long t, RowFlags flags, double vx = 0.0, double vy = 0.0, double totalMmX = 0.0, double totalMmY = 0.0, ScrollEndReason reason = ScrollEndReason.None)
	{
		(int X, int Y, int Screen) pointer = device.Pointer;
		int item = pointer.X;
		int item2 = pointer.Y;
		int item3 = pointer.Screen;
		EventRow row = new EventRow
		{
			Kind = RowKind.Scroll,
			Flags = (RowFlags.IsPrecise | flags),
			WallMs = t,
			OsTime = device.LastOsTime,
			SourcePid = 0,
			App = _foreground(),
			X = item,
			Y = item2,
			Screen = item3,
			DeltaX = dx,
			DeltaY = dy,
			Phase = phase,
			Channel = ScrollChannel.Touchpad,
			GestureId = gestureId,
			Modifiers = _modifiers(),
			VelocityX = vx,
			VelocityY = vy,
			TotalMmX = totalMmX,
			TotalMmY = totalMmY,
			EndReason = reason
		};
		Enqueue(in row);
		Interlocked.Increment(ref _counters.ScrollRows);
	}

	private void OnLiftTimer()
	{
		long timestamp = Stopwatch.GetTimestamp();
		uint messageTime = (uint)GetMessageTime();
		foreach (PtpDevice value in _devices.Values)
		{
			if (value.Assembler.ContactsDown && RecordingClock.MsBetween(value.Assembler.LastReportQpc, timestamp) >= 100)
			{
				value.Assembler.Timeout(timestamp, messageTime);
			}
		}
		if (!AnyContactsDown())
		{
			KillTimer(Volatile.Read(in _windowHandle), 1u);
			_liftTimerArmed = false;
		}
	}

	private void OnSettings(int reasonIndex)
	{
		if (reasonIndex < 0 || reasonIndex >= SettingsReasons.Length)
		{
			return;
		}
		string text = SettingsReasons[reasonIndex];
		if (text == "resume")
		{
			long timestamp = Stopwatch.GetTimestamp();
			uint messageTime = (uint)GetMessageTime();
			foreach (PtpDevice value in _devices.Values)
			{
				value.Assembler.Timeout(timestamp, messageTime);
			}
		}
		InputSettingsSnapshot inputSettingsSnapshot = InputSettingsSnapshot.Read();
		InputSettingsSnapshot inputSettingsSnapshot2 = Volatile.Read(in _settings);
		if (inputSettingsSnapshot2 != null && inputSettingsSnapshot2.Serialized == inputSettingsSnapshot.Serialized)
		{
			return;
		}
		Volatile.Write(ref _settings, inputSettingsSnapshot);
		long num = NowMs();
		Log(inputSettingsSnapshot.ToRow(text, num), num);
		TouchpadSettingsSnapshot touchpadSettingsSnapshot = inputSettingsSnapshot2?.Touchpad;
		TouchpadSettingsSnapshot touchpad = inputSettingsSnapshot.Touchpad;
		bool flag = touchpadSettingsSnapshot == null || touchpadSettingsSnapshot.Reversed != touchpad.Reversed || touchpadSettingsSnapshot.Pan != touchpad.Pan || touchpadSettingsSnapshot.Zoom != touchpad.Zoom || touchpadSettingsSnapshot.Active != touchpad.Active;
		foreach (PtpDevice value2 in _devices.Values)
		{
			value2.Record = value2.Record.WithSettings(touchpad);
			if (flag)
			{
				Log(value2.Record.ToRow("change", num, includePreparsed: false), num);
			}
		}
		PublishRecords();
	}

	private void OnEndGestures()
	{
		long t = NowMs();
		foreach (PtpDevice value in _devices.Values)
		{
			value.Machine.End(t, ScrollEndReason.SegmentClose);
			PublishGesture(value, 0L);
			value.FlushPending();
		}
	}

	private void OnStop()
	{
		OnEndGestures();
		PostQuitMessage(0);
	}

	private void Enqueue(in EventRow row)
	{
		_sink()?.Enqueue(in row);
	}

	private void Log(object payload, long timestampMs)
	{
		_sink()?.Log(payload, timestampMs);
	}

	private static long NowMs()
	{
		return (long)RecordingClock.Shared.NowMs();
	}
}
