using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using ClientTelemetry;
using KuroWindows.Input.Touchpad;
using KuroWindows.Input.Uia;
using KuroWindows.RecorderCore.Clock;
using KuroWindows.Sync;
using Microsoft.Win32;

namespace KuroWindows.Input;

public sealed class InputMonitor : IDisposable
{
	private delegate nint HookProc(int nCode, nint wParam, nint lParam);

	private delegate void WinEventDelegate(nint hook, uint evt, nint hwnd, int obj, int child, uint thread, uint time);

	private delegate bool EnumWindowsProc(nint hwnd, nint lParam);

	private struct POINT
	{
		public int x;

		public int y;
	}

	private struct MSLLHOOKSTRUCT
	{
		public POINT pt;

		public uint mouseData;

		public uint flags;

		public uint time;

		public nint extra;
	}

	private struct KBDLLHOOKSTRUCT
	{
		public uint vkCode;

		public uint scanCode;

		public uint flags;

		public uint time;

		public nint extra;
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

	private struct RECT
	{
		public int Left;

		public int Top;

		public int Right;

		public int Bottom;
	}

	private struct WindowRectState
	{
		public int X;

		public int Y;

		public int Width;

		public int Height;

		public long LastEmitMs;

		public long FirstSeenMs;
	}

	[StructLayout(LayoutKind.Explicit)]
	private struct RAWMOUSE
	{
		[FieldOffset(0)]
		public ushort usFlags;

		[FieldOffset(4)]
		public ushort usButtonFlags;

		[FieldOffset(6)]
		public ushort usButtonData;

		[FieldOffset(8)]
		public uint ulRawButtons;

		[FieldOffset(12)]
		public int lLastX;

		[FieldOffset(16)]
		public int lLastY;

		[FieldOffset(20)]
		public uint ulExtraInformation;
	}

	private struct RAWINPUT
	{
		public HidInterop.RAWINPUTHEADER header;

		public RAWMOUSE mouse;
	}

	private sealed class ProcessEntry
	{
		public nint Handle;

		public string Path = "";

		public string Exe = "";

		public long LastSeenMs;

		public bool Launched;

		public bool AumidRead;

		public string? Aumid;
	}

	private struct ClickChain
	{
		public long DownMs;

		public int X;

		public int Y;

		public nint Root;

		public int Count;
	}

	private readonly record struct RearmLine(bool Installed, bool Mouse, bool Keyboard, int LastError, int Channel, long SilenceMs, int Witness);

	private sealed record InstallLine(string Text, bool Failed);

	private sealed class SecureFieldRequest
	{
		public readonly ManualResetEventSlim Done = new ManualResetEventSlim(initialState: false);

		public volatile bool IsSecure;
	}

	private readonly record struct WindowIdentity(string Name, string Exe, string? Path, uint Pid, string? Aumid, bool HostedPending);

	private sealed record ClipboardMarker(long WallMs, AppContext App);

	private enum ProcessSighting
	{
		None,
		Window,
		Foreground
	}

	private sealed class InputWindow : NativeWindow
	{
		private readonly InputMonitor _monitor;

		internal InputWindow(InputMonitor monitor)
		{
			_monitor = monitor;
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
			switch (m.Msg)
			{
			case 255:
				_monitor.OnRawInput(m.LParam);
				break;
			case 254:
				_monitor.OnDeviceChange(m.WParam, m.LParam);
				break;
			case 797:
				_monitor.OnClipboardChanged();
				break;
			case 275:
				switch ((int)m.WParam)
				{
				case 1:
					_monitor.PollWindowTitle();
					break;
				case 2:
					_monitor.OnForegroundRecheck();
					break;
				case 3:
					_monitor.OnForegroundCompare();
					break;
				case 4:
					_monitor.PollExitedProcesses();
					break;
				}
				break;
			case 32771:
				_monitor.ReconcileButtonsNudge();
				break;
			case 32773:
				_monitor.ApplyPendingHeldState();
				break;
			case 32772:
				_monitor.OnAppNameResolved();
				break;
			case 32770:
				_monitor.SetForegroundCompare(m.WParam != IntPtr.Zero);
				break;
			}
			base.WndProc(ref m);
		}
	}

	private const ushort MOUSE_MOVE_ABSOLUTE = 1;

	private const int WH_MOUSE_LL = 14;

	private const int WH_KEYBOARD_LL = 13;

	private const int WM_MOUSEMOVE = 512;

	private const int WM_LBUTTONDOWN = 513;

	private const int WM_LBUTTONUP = 514;

	private const int WM_RBUTTONDOWN = 516;

	private const int WM_RBUTTONUP = 517;

	private const int WM_MBUTTONDOWN = 519;

	private const int WM_MBUTTONUP = 520;

	private const int WM_XBUTTONDOWN = 523;

	private const int WM_XBUTTONUP = 524;

	private const int XBUTTON1 = 1;

	private const int XBUTTON2 = 2;

	private const int WM_MOUSEWHEEL = 522;

	private const int WM_MOUSEHWHEEL = 526;

	private const int WM_KEYDOWN = 256;

	private const int WM_KEYUP = 257;

	private const int WM_SYSKEYDOWN = 260;

	private const int WM_SYSKEYUP = 261;

	private const uint LLMHF_INJECTED = 1u;

	private const uint LLMHF_LOWER_IL_INJECTED = 2u;

	private const uint LLKHF_EXTENDED = 1u;

	private const uint LLKHF_LOWER_IL_INJECTED = 2u;

	private const uint LLKHF_INJECTED = 16u;

	private const ulong TouchSignatureMask = 4294967040uL;

	private const ulong TouchSignature = 4283520768uL;

	private const int WM_CLIPBOARDUPDATE = 797;

	private const int WM_TIMER = 275;

	private const int TitleTimerId = 1;

	private const int ForegroundRecheckTimerId = 2;

	private const int ForegroundCompareTimerId = 3;

	private const int ProcessExitTimerId = 4;

	private const uint ForegroundRecheckMs = 250u;

	private const uint ForegroundCompareMs = 2000u;

	private const uint ProcessExitPollMs = 5000u;

	private const int VK_LBUTTON = 1;

	private const int VK_RBUTTON = 2;

	private const int VK_MBUTTON = 4;

	private const int VK_XBUTTON1 = 5;

	private const int VK_XBUTTON2 = 6;

	private const int VK_SHIFT = 16;

	private const int VK_CONTROL = 17;

	private const int VK_MENU = 18;

	private const int VK_LWIN = 91;

	private const int VK_RWIN = 92;

	private const int VK_CAPITAL = 20;

	private const int VK_NUMLOCK = 144;

	private const int VK_SCROLL = 145;

	private const uint VK_PACKET = 231u;

	private const int CWP_SKIPINVISIBLE = 1;

	private const int CWP_SKIPDISABLED = 2;

	private const int CWP_SKIPTRANSPARENT = 4;

	private const uint GA_ROOT = 2u;

	private const int SYNCHRONIZE = 1048576;

	private const uint WAIT_OBJECT_0 = 0u;

	private const string FrameHostExe = "ApplicationFrameHost.exe";

	private const string CoreWindowClass = "Windows.UI.Core.CoreWindow";

	private const int VK_LSHIFT = 160;

	private const int VK_RSHIFT = 161;

	private const int VK_LCONTROL = 162;

	private const int VK_RCONTROL = 163;

	private const int VK_LMENU = 164;

	private const int VK_RMENU = 165;

	private const uint EVENT_SYSTEM_FOREGROUND = 3u;

	private const uint EVENT_SYSTEM_MINIMIZESTART = 22u;

	private const uint EVENT_SYSTEM_MINIMIZEEND = 23u;

	private const uint EVENT_SYSTEM_DESKTOPSWITCH = 32u;

	private const uint EVENT_OBJECT_LOCATIONCHANGE = 32779u;

	private const uint EVENT_OBJECT_SHOW = 32770u;

	private const uint EVENT_OBJECT_HIDE = 32771u;

	private const uint EVENT_OBJECT_CLOAKED = 32791u;

	private const uint EVENT_OBJECT_UNCLOAKED = 32792u;

	private const uint EVENT_OBJECT_IME_SHOW = 32807u;

	private const uint EVENT_OBJECT_IME_HIDE = 32808u;

	private const uint WINEVENT_OUTOFCONTEXT = 0u;

	private const uint WINEVENT_SKIPOWNPROCESS = 2u;

	private const int PROCESS_QUERY_LIMITED_INFORMATION = 4096;

	private const uint TOKEN_QUERY = 8u;

	private const int TokenIntegrityLevel = 25;

	private const uint CF_TEXT = 1u;

	private const uint CF_BITMAP = 2u;

	private const uint CF_DIB = 8u;

	private const uint CF_UNICODETEXT = 13u;

	private const uint CF_HDROP = 15u;

	private const uint CF_DIBV5 = 17u;

	private const uint WM_QUIT = 18u;

	private const uint WM_APP = 32768u;

	private const uint WM_REARM_HOOKS = 32769u;

	private const uint WM_FOREGROUND_COMPARE = 32770u;

	private const uint WM_RECONCILE_BUTTONS = 32771u;

	private const uint WM_NAME_RESOLVED = 32772u;

	private const uint WM_SEED_HELD_STATE = 32773u;

	private const uint DESKTOP_READOBJECTS = 1u;

	private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

	private const int DWMWA_CLOAKED = 14;

	private const long WindowLocationInitialNoiseMs = 800L;

	private const long WindowLocationThrottleMs = 250L;

	private const int GWL_EXSTYLE = -20;

	private const long WS_EX_TOOLWINDOW = 128L;

	private const uint GW_OWNER = 4u;

	private IEventSink? _eventSink;

	private Thread? _hookThread;

	private uint _hookThreadId;

	private readonly ManualResetEventSlim _hookThreadIdPublished = new ManualResetEventSlim(initialState: false);

	private readonly ManualResetEventSlim _installed = new ManualResetEventSlim(initialState: false);

	private HookProc? _mouseProc;

	private HookProc? _keyProc;

	private WinEventDelegate? _winEventProc;

	private WinEventDelegate? _locationEventProc;

	private WinEventDelegate? _visibilityEventProc;

	private WinEventDelegate? _desktopSwitchProc;

	private WinEventDelegate? _windowStateProc;

	private WinEventDelegate? _imeProc;

	private nint _mouseHook;

	private nint _keyHook;

	private nint _winEventHook;

	private nint _locationHook;

	private nint _visibilityHook;

	private nint _desktopSwitchHook;

	private nint _minimizeHook;

	private nint _cloakHook;

	private nint _imeHook;

	private bool _hooksInstalled;

	private bool _winEventsInstalled;

	private bool _clipboardListening;

	private bool _rawMouseRegistered;

	private bool _rawKeyboardRegistered;

	private nint _desktopWindow;

	private nint _lastForegroundHwnd;

	private bool _foregroundCompareArmed;

	private bool _hostedAppPending;

	private readonly long[] _hookDeliveredQpc = new long[2];

	private readonly long[] _witnessQpc = new long[2];

	private long _rearmQpc;

	private readonly int[] _witnessSinceDelivery = new int[2];

	private readonly bool[] _rearmFailedLogged = new bool[2];

	private const int MouseChannel = 0;

	private const int KeyboardChannel = 1;

	private static readonly string[] ChannelNames = new string[2] { "mouse", "keyboard" };

	private const long WitnessSilenceMs = 2000L;

	private const int WitnessPackets = 3;

	private const long FallbackSilenceMs = 1000L;

	private long _ownIntegrityRid;

	private readonly Dictionary<uint, bool> _elevatedByPid = new Dictionary<uint, bool>();

	private const int MaxElevatedPids = 256;

	private AppContext? _foregroundElevated;

	private int _secureDesktop;

	private int _sessionBlind;

	private int _desktopSwitched;

	private readonly Dictionary<nint, WindowRectState> _windowRects = new Dictionary<nint, WindowRectState>();

	private readonly Dictionary<uint, HashSet<nint>> _visibleByPid = new Dictionary<uint, HashSet<nint>>();

	private const int MaxVisiblePids = 256;

	private bool _imeShown;

	private readonly Dictionary<uint, ProcessEntry> _processes = new Dictionary<uint, ProcessEntry>();

	private const int MaxProcessHandles = 64;

	private readonly Dictionary<nint, AppContext> _targets = new Dictionary<nint, AppContext>();

	private const int MaxTargets = 256;

	private long _lastMouseMoveMs;

	private long _lastDragMs;

	private int _held;

	private long _heldSinceQpc;

	private InputStateSnapshot? _pendingSeed;

	private int _lastMouseScreenIndex = -1;

	private readonly ClickChain[] _clickChains = new ClickChain[6];

	private readonly HashSet<uint> _pressedKeys = new HashSet<uint>();

	private static readonly uint[] ModifierVks = new uint[11]
	{
		16u, 17u, 18u, 91u, 92u, 160u, 161u, 162u, 163u, 164u,
		165u
	};

	private int _modifierState;

	private uint _lastEmittedModifiers;

	private const int ModLShift = 1;

	private const int ModRShift = 2;

	private const int ModLControl = 4;

	private const int ModRControl = 8;

	private const int ModLMenu = 16;

	private const int ModRMenu = 32;

	private const int ModLWin = 64;

	private const int ModRWin = 128;

	private const int ModCaps = 256;

	private readonly byte[] _keyState = new byte[256];

	private readonly char[] _keyChars = new char[4];

	private nint _lastHkl;

	private char _pendingDeadKey;

	private char _pendingHighSurrogate;

	private static readonly Dictionary<char, char> CombiningMarks = new Dictionary<char, char>
	{
		['\u00b4'] = '\u0301',
		['`'] = '\u0300',
		['^'] = '\u0302',
		['~'] = '\u0303',
		['\u00a8'] = '\u0308',
		['\u00b8'] = '\u0327',
		['\u02da'] = '\u030a',
		['ˇ'] = '\u030c',
		['\u02d8'] = '\u0306',
		['\u02db'] = '\u0328',
		['\u02d9'] = '\u0307',
		['\u00af'] = '\u0304',
		['\u02dd'] = '\u030b'
	};

	private nint _rawInputBuffer;

	private uint _rawInputBufferSize;

	private int _mouseDeltaFlushMs;

	private readonly InputFactsAccumulator _inputFacts = new InputFactsAccumulator();

	private TouchpadChannel? _touchpad;

	private long _lastOsWheelQpc;

	private AppContext _app = AppContext.Unknown;

	private ActiveAppSnapshot? _activeApp;

	private string? _lastBrowserUrl;

	private bool? _lastSecureFieldFocused;

	private bool _titlePollFailureLogged;

	private readonly ConcurrentDictionary<string, string?> _appNames = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

	private readonly ConcurrentQueue<string> _appNameRequests = new ConcurrentQueue<string>();

	private readonly AutoResetEvent _appNameWake = new AutoResetEvent(initialState: false);

	private readonly ConcurrentQueue<(long Hkl, long WallMs)> _inputSourceRequests = new ConcurrentQueue<(long, long)>();

	private readonly Dictionary<long, (string Name, string Id)> _layoutNames = new Dictionary<long, (string, string)>();

	private Thread? _appNameThread;

	private volatile bool _appNameRunning;

	private readonly ConcurrentQueue<RearmLine> _rearmLines = new ConcurrentQueue<RearmLine>();

	private InstallLine? _installLine;

	private string? _foregroundPath;

	private nint _inputWindowHandle;

	private Thread? _uiaThread;

	private readonly AutoResetEvent _uiaWake = new AutoResetEvent(initialState: false);

	private volatile bool _uiaRunning;

	private int _uiaForegroundProbePending;

	private readonly Queue<SecureFieldRequest> _uiaSyncRequests = new Queue<SecureFieldRequest>();

	private volatile DesktopFeatureFlags _featureFlags = DesktopFeatureFlags.Defaults;

	private bool _uiaSlowOpLogged;

	private bool _uiaBridgeTimeoutLogged;

	private int _suppressKeys;

	public Action<ForegroundWindowInfo>? ForegroundChanged;

	public Action<ForegroundWindowInfo>? WindowTitleChanged;

	public Action<bool>? SecureFieldChanged;

	public Action<InputKind>? OnUserInteraction;

	private FrameStateSource? _frameState;

	private readonly ConcurrentDictionary<string, byte> _callbackFailuresLogged = new ConcurrentDictionary<string, byte>();

	private static int _appInfoResolveDeniedLogged;

	private int _unresolvedLineOwed;

	private uint _unresolvedPid;

	public bool SuppressKeys
	{
		get
		{
			return Volatile.Read(in _suppressKeys) != 0;
		}
		set
		{
			int num = (value ? 1 : 0);
			if (Interlocked.Exchange(ref _suppressKeys, num) != num && value)
			{
				ClearKeyboardStateForPrivacy();
			}
		}
	}

	internal TouchpadChannel? Touchpad => _touchpad;

	internal AppContext CurrentApp => Volatile.Read(in _app);

	internal ActiveAppSnapshot? CurrentWindow => Volatile.Read(in _activeApp);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern nint SetWindowsHookEx(int id, HookProc fn, nint mod, uint tid);

	[DllImport("user32.dll")]
	private static extern bool UnhookWindowsHookEx(nint h);

	[DllImport("user32.dll")]
	private static extern nint CallNextHookEx(nint h, int code, nint w, nint l);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern nint SetWinEventHook(uint min, uint max, nint mod, WinEventDelegate fn, uint pid, uint tid, uint flags);

	[DllImport("user32.dll")]
	private static extern bool UnhookWinEvent(nint h);

	[DllImport("user32.dll")]
	private static extern int GetMessage(out MSG msg, nint hwnd, uint min, uint max);

	[DllImport("user32.dll")]
	private static extern bool TranslateMessage(ref MSG msg);

	[DllImport("user32.dll")]
	private static extern nint DispatchMessage(ref MSG msg);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern bool PostThreadMessage(uint tid, uint msg, nint w, nint l);

	[DllImport("user32.dll")]
	private static extern bool PostMessage(nint hwnd, uint msg, nint w, nint l);

	[DllImport("user32.dll")]
	private static extern int GetMessageTime();

	[DllImport("user32.dll")]
	private static extern nuint SetTimer(nint hwnd, nuint id, uint elapseMs, nint proc);

	[DllImport("user32.dll")]
	private static extern bool KillTimer(nint hwnd, nuint id);

	[DllImport("user32.dll")]
	private static extern nint OpenInputDesktop(uint flags, bool inherit, uint access);

	[DllImport("user32.dll")]
	private static extern bool CloseDesktop(nint desktop);

	[DllImport("kernel32.dll")]
	private static extern uint GetCurrentThreadId();

	[DllImport("user32.dll")]
	private static extern short GetKeyState(int vk);

	[DllImport("user32.dll")]
	private static extern short GetAsyncKeyState(int vk);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern int ToUnicodeEx(uint vk, uint scan, byte[] state, [Out] char[] buf, int sz, uint flags, nint hkl);

	[DllImport("user32.dll")]
	private static extern nint GetKeyboardLayout(uint tid);

	[DllImport("user32.dll")]
	private static extern nint GetForegroundWindow();

	[DllImport("user32.dll")]
	private static extern nint GetDesktopWindow();

	[DllImport("user32.dll")]
	private static extern nint ChildWindowFromPointEx(nint parent, POINT pt, uint flags);

	[DllImport("user32.dll")]
	private static extern nint GetAncestor(nint hwnd, uint flags);

	[DllImport("user32.dll")]
	private static extern bool IsIconic(nint hwnd);

	[DllImport("user32.dll")]
	private static extern bool EnumWindows(EnumWindowsProc fn, nint lParam);

	[DllImport("user32.dll")]
	private static extern bool EnumChildWindows(nint parent, EnumWindowsProc fn, nint lParam);

	[DllImport("user32.dll")]
	private static extern nint GetWindowLongPtr(nint hWnd, int nIndex);

	[DllImport("user32.dll")]
	private static extern nint GetWindow(nint hWnd, uint uCmd);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern int GetWindowText(nint h, StringBuilder s, int n);

	[DllImport("user32.dll")]
	private static extern uint GetWindowThreadProcessId(nint h, out uint pid);

	[DllImport("kernel32.dll")]
	private static extern nint OpenProcess(int access, bool inherit, uint pid);

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
	private static extern bool QueryFullProcessImageName(nint h, int flags, StringBuilder buf, ref int sz);

	[DllImport("kernel32.dll")]
	private static extern bool CloseHandle(nint h);

	[DllImport("kernel32.dll")]
	private static extern uint WaitForSingleObject(nint h, uint ms);

	[DllImport("kernel32.dll")]
	private static extern nint GetCurrentProcess();

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
	private static extern int GetApplicationUserModelId(nint process, ref uint length, StringBuilder id);

	[DllImport("advapi32.dll", SetLastError = true)]
	private static extern bool OpenProcessToken(nint process, uint access, out nint token);

	[DllImport("advapi32.dll", SetLastError = true)]
	private static extern bool GetTokenInformation(nint token, int infoClass, nint info, uint length, out uint returnLength);

	[DllImport("advapi32.dll")]
	private static extern nint GetSidSubAuthority(nint sid, uint index);

	[DllImport("advapi32.dll")]
	private static extern nint GetSidSubAuthorityCount(nint sid);

	[DllImport("user32.dll")]
	private static extern bool AddClipboardFormatListener(nint h);

	[DllImport("user32.dll")]
	private static extern bool RemoveClipboardFormatListener(nint h);

	[DllImport("user32.dll")]
	private static extern bool IsClipboardFormatAvailable(uint fmt);

	[DllImport("user32.dll")]
	private static extern bool OpenClipboard(nint h);

	[DllImport("user32.dll")]
	private static extern bool CloseClipboard();

	[DllImport("user32.dll")]
	private static extern nint GetClipboardData(uint fmt);

	[DllImport("user32.dll")]
	private static extern uint GetClipboardSequenceNumber();

	[DllImport("user32.dll")]
	private static extern uint EnumClipboardFormats(uint fmt);

	[DllImport("kernel32.dll")]
	private static extern nuint GlobalSize(nint h);

	[DllImport("kernel32.dll")]
	private static extern nint GlobalLock(nint h);

	[DllImport("kernel32.dll")]
	private static extern bool GlobalUnlock(nint h);

	[DllImport("user32.dll")]
	private static extern bool GetWindowRect(nint h, out RECT rect);

	[DllImport("user32.dll")]
	private static extern bool IsWindowVisible(nint hWnd);

	[DllImport("user32.dll")]
	private static extern bool IsWindow(nint hWnd);

	[DllImport("dwmapi.dll")]
	private static extern int DwmGetWindowAttribute(nint hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);

	[DllImport("dwmapi.dll")]
	private static extern int DwmGetWindowAttribute(nint hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);

	public void ApplyFeatureFlags(DesktopFeatureFlags? flags)
	{
		_featureFlags = flags ?? DesktopFeatureFlags.Defaults;
		UiaProbeHost.Shared.SetEnabled(_featureFlags.UiaProbeEnabled);
		if (!_featureFlags.UiaSecureFieldEnabled)
		{
			RequestSecureFieldProbe();
		}
	}

	public void SetFrameStateSource(FrameStateSource? source)
	{
		_frameState = source;
	}

	internal InputFactsSnapshot SnapshotAndResetInputFacts()
	{
		InputFactsSnapshot inputFactsSnapshot = _inputFacts.SnapshotAndReset();
		TouchChannelFacts touchChannelFacts = _touchpad?.SnapshotAndReset();
		if (!(touchChannelFacts == null))
		{
			return inputFactsSnapshot with
			{
				GamepadPackets = touchChannelFacts.GamepadPackets,
				Touchpad = touchChannelFacts.Touchpad
			};
		}
		return inputFactsSnapshot;
	}

	public void SetEventSink(IEventSink? sink)
	{
		Volatile.Write(ref _eventSink, sink);
		nint num = Volatile.Read(in _inputWindowHandle);
		if (num != IntPtr.Zero)
		{
			PostMessage(num, 32770u, (sink != null) ? new IntPtr(1) : IntPtr.Zero, IntPtr.Zero);
		}
	}

	internal bool WaitForInstall(int timeoutMs)
	{
		long timestamp = Stopwatch.GetTimestamp();
		if (!_installed.Wait(timeoutMs))
		{
			return false;
		}
		int val = timeoutMs - (int)RecordingClock.MsBetween(timestamp, Stopwatch.GetTimestamp());
		if (_touchpad != null)
		{
			return _touchpad.WaitForRegistration(Math.Max(0, val));
		}
		return true;
	}

	internal ChannelCapabilities Channels()
	{
		return new ChannelCapabilities(Volatile.Read(in _hooksInstalled), Volatile.Read(in _rawMouseRegistered), Volatile.Read(in _rawKeyboardRegistered), _touchpad?.Registered ?? false, Volatile.Read(in _winEventsInstalled), Volatile.Read(in _clipboardListening));
	}

	internal long LastDeliveryQpc()
	{
		return Math.Max(Math.Max(Math.Max(Volatile.Read(in _hookDeliveredQpc[0]), Volatile.Read(in _hookDeliveredQpc[1])), Math.Max(Volatile.Read(in _witnessQpc[0]), Volatile.Read(in _witnessQpc[1]))), _touchpad?.LastReportQpc ?? 0);
	}

	internal long LastRearmQpc()
	{
		return Volatile.Read(in _rearmQpc);
	}

	internal BlindFlags BlindFlags()
	{
		AppContext appContext = Volatile.Read(in _foregroundElevated);
		int num = Volatile.Read(in _sessionBlind);
		return new BlindFlags(appContext != null, appContext?.Pid ?? 0, appContext?.AppName ?? "", Interlocked.Exchange(ref _desktopSwitched, 0) != 0, Volatile.Read(in _secureDesktop) != 0, num != 0, num == 2);
	}

	internal void SetSessionInteractive(bool interactive, SessionInactiveReason reason = SessionInactiveReason.Lock)
	{
		int value;
		if (interactive)
		{
			value = 0;
		}
		else
		{
			bool flag = (uint)(reason - 2) <= 1u;
			value = ((!flag) ? 1 : 2);
		}
		Volatile.Write(ref _sessionBlind, value);
	}

	internal bool ProbeSecureDesktop()
	{
		bool flag = SecureDesktopOwnsInput();
		Volatile.Write(ref _secureDesktop, flag ? 1 : 0);
		return flag;
	}

	internal void PostRearm()
	{
		uint num = Volatile.Read(in _hookThreadId);
		if (num != 0)
		{
			PostThreadMessage(num, 32769u, IntPtr.Zero, IntPtr.Zero);
		}
	}

	internal void PostReconcileButtons()
	{
		nint num = Volatile.Read(in _inputWindowHandle);
		if (num != IntPtr.Zero)
		{
			PostMessage(num, 32771u, IntPtr.Zero, IntPtr.Zero);
		}
	}

	internal long ButtonHeldSinceQpc()
	{
		return Volatile.Read(in _heldSinceQpc);
	}

	internal InputStateSnapshot? SnapshotHeldState()
	{
		if (IsBlind())
		{
			return null;
		}
		long timestamp = Stopwatch.GetTimestamp();
		bool swapped = SwapButtons();
		List<uint> list = new List<uint>();
		for (int i = 8; i <= 254; i++)
		{
			if (KeyHeld(i))
			{
				list.Add((uint)i);
			}
		}
		bool flag = (GetKeyState(20) & 1) != 0;
		uint num = 0u;
		if (KeyHeld(16))
		{
			num |= 1;
		}
		if (KeyHeld(17))
		{
			num |= 2;
		}
		if (KeyHeld(18))
		{
			num |= 4;
		}
		if (KeyHeld(91) || KeyHeld(92))
		{
			num |= 8;
		}
		if (flag)
		{
			num |= 0x10;
		}
		return new InputStateSnapshot(timestamp, list.ToArray(), KeyHeld(ButtonVk(MouseButton.Left, swapped)), KeyHeld(ButtonVk(MouseButton.Right, swapped)), KeyHeld(4), KeyHeld(5), KeyHeld(6), flag, (GetKeyState(144) & 1) != 0, (GetKeyState(145) & 1) != 0, num);
	}

	private bool SwapButtons()
	{
		return _touchpad?.Settings?.SwapButtons == true;
	}

	internal void SeedHeldState(InputStateSnapshot s)
	{
		Interlocked.Exchange(ref _pendingSeed, s);
		nint num = Volatile.Read(in _inputWindowHandle);
		if (num != IntPtr.Zero)
		{
			PostMessage(num, 32773u, IntPtr.Zero, IntPtr.Zero);
		}
	}

	private void ApplyPendingHeldState()
	{
		try
		{
			InputStateSnapshot inputStateSnapshot = Interlocked.Exchange(ref _pendingSeed, null);
			if (!(inputStateSnapshot == null))
			{
				if (Volatile.Read(in _hookDeliveredQpc[0]) < inputStateSnapshot.Qpc)
				{
					SeedButtons(inputStateSnapshot);
				}
				if (Volatile.Read(in _hookDeliveredQpc[1]) < inputStateSnapshot.Qpc)
				{
					SeedKeys(inputStateSnapshot);
				}
			}
		}
		catch (Exception ex)
		{
			NoteCallbackFailure("seed_held_state", ex);
		}
	}

	private void SeedButtons(InputStateSnapshot s)
	{
		int num = 0;
		if (s.Left)
		{
			num = HeldAdd(num, MouseButton.Left);
		}
		if (s.Right)
		{
			num = HeldAdd(num, MouseButton.Right);
		}
		if (s.Middle)
		{
			num = HeldAdd(num, MouseButton.Middle);
		}
		if (s.Other3)
		{
			num = HeldAdd(num, MouseButton.Other3);
		}
		if (s.Other4)
		{
			num = HeldAdd(num, MouseButton.Other4);
		}
		Volatile.Write(ref _held, num);
		Volatile.Write(ref _heldSinceQpc, (num == 0) ? 0 : Stopwatch.GetTimestamp());
		FrameStateSource frameState = _frameState;
		if (frameState != null)
		{
			frameState.UpdateMouseButton("left", s.Left);
			frameState.UpdateMouseButton("right", s.Right);
			frameState.UpdateMouseButton("middle", s.Middle);
			frameState.UpdateMouseButton("other_3", s.Other3);
			frameState.UpdateMouseButton("other_4", s.Other4);
		}
	}

	private void SeedKeys(InputStateSnapshot s)
	{
		uint[] array = s.KeysDown.Where((uint vk) => Array.IndexOf(ModifierVks, vk) < 0).ToArray();
		uint[] array2;
		lock (_pressedKeys)
		{
			_pressedKeys.Clear();
			array2 = array;
			foreach (uint item in array2)
			{
				_pressedKeys.Add(item);
			}
		}
		int num2 = (s.Caps ? 256 : 0);
		array2 = s.KeysDown;
		foreach (uint num3 in array2)
		{
			if (num3 - 16 > 2)
			{
				num2 |= ModifierBit(num3);
			}
		}
		Volatile.Write(ref _modifierState, num2);
		Volatile.Write(ref _lastEmittedModifiers, ModifierMask(num2));
		_frameState?.SeedKeyboardState(array, s.Modifiers);
	}

	internal static bool ShouldRearm(long silenceMs, int witnessCount, bool blind)
	{
		if (silenceMs > 2000 && witnessCount >= 3)
		{
			return !blind;
		}
		return false;
	}

	private static bool KeyHeld(int vk)
	{
		return (GetAsyncKeyState(vk) & 0x8000) != 0;
	}

	private static int ButtonVk(MouseButton button, bool swapped)
	{
		return button switch
		{
			MouseButton.Left => (!swapped) ? 1 : 2, 
			MouseButton.Right => swapped ? 1 : 2, 
			MouseButton.Middle => 4, 
			MouseButton.Other3 => 5, 
			_ => 6, 
		};
	}

	private static string ButtonName(MouseButton button)
	{
		return button switch
		{
			MouseButton.Left => "left", 
			MouseButton.Right => "right", 
			MouseButton.Middle => "middle", 
			MouseButton.Other3 => "other_3", 
			_ => "other_4", 
		};
	}

	private static bool HeldContains(int held, MouseButton button)
	{
		for (int num = held; num != 0; num >>= 3)
		{
			if ((num & 7) == (int)button)
			{
				return true;
			}
		}
		return false;
	}

	private static int HeldAdd(int held, MouseButton button)
	{
		if (HeldContains(held, button))
		{
			return held;
		}
		int i;
		for (i = 0; ((held >> i) & 7) != 0; i += 3)
		{
		}
		return held | (int)((uint)button << i);
	}

	private static int HeldRemove(int held, MouseButton button)
	{
		int num = 0;
		int num2 = 0;
		for (int num3 = held; num3 != 0; num3 >>= 3)
		{
			int num4 = num3 & 7;
			if (num4 != (int)button)
			{
				num |= num4 << num2;
				num2 += 3;
			}
		}
		return num;
	}

	private static MouseButton HeldFirst(int held)
	{
		return (MouseButton)(held & 7);
	}

	public void Start()
	{
		MonitorTable.Invalidate();
		_touchpad = new TouchpadChannel(() => Volatile.Read(in _eventSink), _frameState, () => Volatile.Read(in _app), () => _frameState?.LastMousePosition(), () => ModifierMask(Volatile.Read(in _modifierState)), (InputKind kind) =>
		{
			OnUserInteraction?.Invoke(kind);
		}, NoteCallbackFailure);
		_touchpad.Start();
		_hookThread = new Thread(HookThreadProc)
		{
			IsBackground = true,
			Name = "KuroHooks",
			Priority = ThreadPriority.AboveNormal
		};
		_hookThread.SetApartmentState(ApartmentState.STA);
		_hookThread.Start();
		Point position = Cursor.Position;
		_lastMouseScreenIndex = MonitorTable.Current.IndexOfNearest(position.X, position.Y);
		_frameState?.UpdateMousePosition(position.X, position.Y, _lastMouseScreenIndex, NowMs());
		_appNameRunning = true;
		_appNameThread = new Thread(AppNameWorkerProc)
		{
			IsBackground = true,
			Name = "KuroAppNames"
		};
		_appNameThread.Start();
		_uiaRunning = true;
		_uiaThread = new Thread(UiaWorkerProc)
		{
			IsBackground = true,
			Name = "KuroUia"
		};
		_uiaThread.SetApartmentState(ApartmentState.MTA);
		_uiaThread.Start();
		UiaProbeHost.Shared.Warm();
	}

	public ForegroundWindowInfo? GetCurrentForegroundInfo()
	{
		nint foregroundWindow = GetForegroundWindow();
		if (foregroundWindow == IntPtr.Zero)
		{
			return null;
		}
		WindowIdentity identity;
		return BuildForegroundWindowInfo(foregroundWindow, out identity, ProcessSighting.None);
	}

	public bool GetCurrentSecureFieldState()
	{
		if (!_uiaRunning)
		{
			return false;
		}
		SecureFieldRequest secureFieldRequest = new SecureFieldRequest();
		lock (_uiaSyncRequests)
		{
			_uiaSyncRequests.Enqueue(secureFieldRequest);
		}
		_uiaWake.Set();
		if (!secureFieldRequest.Done.Wait(500))
		{
			if (!_uiaBridgeTimeoutLogged)
			{
				_uiaBridgeTimeoutLogged = true;
				KuroLog.SystemEvent("uia secure-field bridge timed out after 500ms; worker busy or wedged, reporting not-secure", "GetCurrentSecureFieldState", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Input\\InputMonitor.cs", 983);
			}
			return false;
		}
		return secureFieldRequest.IsSecure;
	}

	public void Stop()
	{
		_touchpad?.Stop();
		_uiaRunning = false;
		_uiaWake.Set();
		Thread thread = Interlocked.Exchange(ref _uiaThread, null);
		if (thread != null && !thread.Join(2000))
		{
			KuroLog.SystemEvent("uia worker join timed out after 2000ms; thread may still be running", "Stop", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Input\\InputMonitor.cs", 1008);
		}
		Thread thread2 = Interlocked.Exchange(ref _hookThread, null);
		if (thread2 != null)
		{
			if (!_hookThreadIdPublished.Wait(1000))
			{
				KuroLog.SystemEvent("input hook thread id not published within 1s; quit post may be missed", "Stop", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Input\\InputMonitor.cs", 1021);
			}
			if (_hookThreadId != 0 && !PostThreadMessage(_hookThreadId, 18u, IntPtr.Zero, IntPtr.Zero))
			{
				KuroLog.SystemEvent($"input hook quit message post failed err={Marshal.GetLastWin32Error()}", "Stop", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Input\\InputMonitor.cs", 1023);
			}
			if (!thread2.Join(3000))
			{
				KuroLog.SystemEvent("input hook thread join timed out after 3000ms; thread may still be running", "Stop", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Input\\InputMonitor.cs", 1025);
			}
		}
		_appNameRunning = false;
		_appNameWake.Set();
		Thread thread3 = Interlocked.Exchange(ref _appNameThread, null);
		if (thread3 != null && !thread3.Join(2000))
		{
			KuroLog.SystemEvent("app name worker join timed out after 2000ms; thread may still be running", "Stop", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Input\\InputMonitor.cs", 1035);
		}
	}

	private void HookThreadProc()
	{
		_hookThreadId = GetCurrentThreadId();
		_hookThreadIdPublished.Set();
		_mouseProc = MouseHookProc;
		_keyProc = KeyHookProc;
		_winEventProc = OnWinEvent;
		_locationEventProc = OnLocationChanged;
		_visibilityEventProc = OnVisibilityChanged;
		_desktopSwitchProc = OnDesktopSwitch;
		_windowStateProc = OnWindowStateEvent;
		_imeProc = OnImeEvent;
		_desktopWindow = GetDesktopWindow();
		_winEventHook = SetWinEventHook(3u, 3u, IntPtr.Zero, _winEventProc, 0u, 0u, 0u);
		_locationHook = SetWinEventHook(32779u, 32779u, IntPtr.Zero, _locationEventProc, 0u, 0u, 2u);
		_visibilityHook = SetWinEventHook(32770u, 32771u, IntPtr.Zero, _visibilityEventProc, 0u, 0u, 2u);
		_desktopSwitchHook = SetWinEventHook(32u, 32u, IntPtr.Zero, _desktopSwitchProc, 0u, 0u, 0u);
		_minimizeHook = SetWinEventHook(22u, 23u, IntPtr.Zero, _windowStateProc, 0u, 0u, 2u);
		_cloakHook = SetWinEventHook(32791u, 32792u, IntPtr.Zero, _windowStateProc, 0u, 0u, 2u);
		_imeHook = SetWinEventHook(32807u, 32808u, IntPtr.Zero, _imeProc, 0u, 0u, 2u);
		InputWindow inputWindow = new InputWindow(this);
		inputWindow.Create();
		Volatile.Write(ref _inputWindowHandle, inputWindow.Handle);
		bool flag = AddClipboardFormatListener(inputWindow.Handle);
		_mouseDeltaFlushMs = AppConfig.MouseDeltaFlushMs;
		_rawInputBufferSize = 1024u;
		_rawInputBuffer = Marshal.AllocHGlobal(1024);
		bool flag2 = RegisterRawInput(2, 8448u, inputWindow.Handle);
		bool flag3 = RegisterRawInput(6, 8448u, inputWindow.Handle);
		Volatile.Write(ref _winEventsInstalled, _winEventHook != IntPtr.Zero && _locationHook != IntPtr.Zero && _visibilityHook != IntPtr.Zero);
		Volatile.Write(ref _clipboardListening, flag);
		Volatile.Write(ref _rawMouseRegistered, flag2);
		Volatile.Write(ref _rawKeyboardRegistered, flag3);
		SetTimer(inputWindow.Handle, 1u, (uint)AppConfig.WindowTitlePollMs, IntPtr.Zero);
		SetTimer(inputWindow.Handle, 4u, 5000u, IntPtr.Zero);
		SetForegroundCompare(Volatile.Read(in _eventSink) != null);
		_ownIntegrityRid = IntegrityRid(GetCurrentProcess());
		nint foregroundWindow = GetForegroundWindow();
		if (foregroundWindow != IntPtr.Zero)
		{
			_lastForegroundHwnd = foregroundWindow;
			PublishForeground(foregroundWindow, RecordingClock.Shared.WallMsFromQpc(Stopwatch.GetTimestamp()));
		}
		_mouseHook = SetWindowsHookEx(14, _mouseProc, IntPtr.Zero, 0u);
		_keyHook = SetWindowsHookEx(13, _keyProc, IntPtr.Zero, 0u);
		Volatile.Write(ref _hooksInstalled, _mouseHook != IntPtr.Zero && _keyHook != IntPtr.Zero);
		bool flag4 = (_mouseHook != IntPtr.Zero && _keyHook != IntPtr.Zero && _winEventHook != IntPtr.Zero && _locationHook != IntPtr.Zero && _visibilityHook != IntPtr.Zero && _desktopSwitchHook != IntPtr.Zero) & flag & flag2 & flag3;
		Volatile.Write(ref _installLine, flag4 ? new InstallLine("input hooks installed; foreground/location/visibility winevent hooks active", Failed: false) : new InstallLine($"input hook install failed mouse={_mouseHook != IntPtr.Zero} keyboard={_keyHook != IntPtr.Zero} foreground={_winEventHook != IntPtr.Zero} location={_locationHook != IntPtr.Zero} visibility={_visibilityHook != IntPtr.Zero} clipboard_listener={flag} raw_input={flag2} last_err={Marshal.GetLastWin32Error()} raw_keyboard={flag3} desktop_switch={_desktopSwitchHook != IntPtr.Zero}", Failed: true));
		_appNameWake.Set();
		_installed.Set();
		long timestamp = Stopwatch.GetTimestamp();
		Volatile.Write(ref _hookDeliveredQpc[0], timestamp);
		Volatile.Write(ref _hookDeliveredQpc[1], timestamp);
		MSG msg;
		while (GetMessage(out msg, IntPtr.Zero, 0u, 0u) > 0)
		{
			if (msg.hwnd == IntPtr.Zero && msg.message == 32769)
			{
				RearmSilentChannels();
				continue;
			}
			TranslateMessage(ref msg);
			DispatchMessage(ref msg);
		}
		KillTimer(inputWindow.Handle, 1u);
		KillTimer(inputWindow.Handle, 2u);
		KillTimer(inputWindow.Handle, 4u);
		SetForegroundCompare(on: false);
		RegisterRawInput(2, 1u, IntPtr.Zero);
		RegisterRawInput(6, 1u, IntPtr.Zero);
		RemoveClipboardFormatListener(inputWindow.Handle);
		Volatile.Write(ref _inputWindowHandle, IntPtr.Zero);
		inputWindow.DestroyHandle();
		Marshal.FreeHGlobal(_rawInputBuffer);
		_rawInputBuffer = IntPtr.Zero;
		UnhookWindowsHookEx(_mouseHook);
		UnhookWindowsHookEx(_keyHook);
		UnhookWinEvent(_winEventHook);
		UnhookWinEvent(_locationHook);
		UnhookWinEvent(_visibilityHook);
		UnhookWinEvent(_desktopSwitchHook);
		UnhookWinEvent(_minimizeHook);
		UnhookWinEvent(_cloakHook);
		UnhookWinEvent(_imeHook);
		foreach (ProcessEntry value in _processes.Values)
		{
			CloseHandle(value.Handle);
		}
		_processes.Clear();
	}

	private static bool RegisterRawInput(ushort usage, uint flags, nint target)
	{
		return HidInterop.RegisterRawInputDevices(new HidInterop.RAWINPUTDEVICE[1]
		{
			new HidInterop.RAWINPUTDEVICE
			{
				usUsagePage = 1,
				usUsage = usage,
				dwFlags = flags,
				hwndTarget = target
			}
		}, 1u, (uint)Marshal.SizeOf<HidInterop.RAWINPUTDEVICE>());
	}

	private void NoteDelivery(int channel, long qpc)
	{
		Volatile.Write(ref _hookDeliveredQpc[channel], qpc);
		_witnessSinceDelivery[channel] = 0;
		Volatile.Write(ref _secureDesktop, 0);
	}

	private void NoteWitness(int channel, long qpc)
	{
		Volatile.Write(ref _witnessQpc[channel], qpc);
		Volatile.Write(ref _secureDesktop, 0);
		int num = ++_witnessSinceDelivery[channel];
		long num2 = Volatile.Read(in _hookDeliveredQpc[channel]);
		long num3 = RecordingClock.MsBetween(num2, qpc);
		if (ShouldRearm(num3, num, IsBlind()))
		{
			bool flag = RearmChannel(channel, num3, num);
			long num4 = RecordingClock.Shared.WallMsFromQpc(Stopwatch.GetTimestamp());
			_inputFacts.NoteGap(num3);
			if (flag)
			{
				WriteRearmState(num4);
			}
			Log(new
			{
				type = "input_gap",
				channel = ChannelNames[channel],
				fromTimestamp = RecordingClock.Shared.WallMsFromQpc(num2),
				gapMs = num3,
				witness = "raw_input",
				action = (flag ? "rearmed" : "rearm_failed"),
				timestamp = num4
			}, num4);
		}
	}

	private void RearmSilentChannels()
	{
		long timestamp = Stopwatch.GetTimestamp();
		long num = RecordingClock.MsBetween(LastDeliveryQpc(), timestamp);
		if (num < 1000)
		{
			return;
		}
		_inputFacts.NoteGap(num);
		bool flag = false;
		for (int i = 0; i < 2; i++)
		{
			if (RearmChannel(i, num, _witnessSinceDelivery[i]))
			{
				flag = true;
			}
		}
		if (flag)
		{
			WriteRearmState(RecordingClock.Shared.WallMsFromQpc(timestamp));
		}
	}

	private bool RearmChannel(int channel, long silenceMs, int witness)
	{
		bool flag = channel == 0;
		nint num = (flag ? _mouseHook : _keyHook);
		if (num != IntPtr.Zero)
		{
			UnhookWindowsHookEx(num);
		}
		num = SetWindowsHookEx(flag ? 14 : 13, flag ? _mouseProc : _keyProc, IntPtr.Zero, 0u);
		int lastWin32Error = Marshal.GetLastWin32Error();
		if (flag)
		{
			_mouseHook = num;
		}
		else
		{
			_keyHook = num;
		}
		bool flag2 = num != IntPtr.Zero;
		Volatile.Write(ref _hooksInstalled, _mouseHook != IntPtr.Zero && _keyHook != IntPtr.Zero);
		long timestamp = Stopwatch.GetTimestamp();
		Volatile.Write(ref _hookDeliveredQpc[channel], timestamp);
		Volatile.Write(ref _rearmQpc, timestamp);
		_witnessSinceDelivery[channel] = 0;
		_inputFacts.NoteRearm();
		if (flag2)
		{
			Count.InputHookRearm(1L);
		}
		if (flag2 || !_rearmFailedLogged[channel])
		{
			_rearmFailedLogged[channel] = !flag2;
			_rearmLines.Enqueue(new RearmLine(flag2, _mouseHook != IntPtr.Zero, _keyHook != IntPtr.Zero, lastWin32Error, channel, silenceMs, witness));
			_appNameWake.Set();
		}
		return flag2;
	}

	private void WriteRearmState(long now)
	{
		InputStateSnapshot inputStateSnapshot = SnapshotHeldState();
		if ((object)inputStateSnapshot != null)
		{
			SeedButtons(inputStateSnapshot);
			SeedKeys(inputStateSnapshot);
			Log(inputStateSnapshot.ToRow("rearm", now), now);
		}
	}

	private bool IsBlind()
	{
		if (!(Volatile.Read(in _foregroundElevated) != null) && Volatile.Read(in _secureDesktop) == 0)
		{
			return Volatile.Read(in _sessionBlind) != 0;
		}
		return true;
	}

	private static bool SecureDesktopOwnsInput()
	{
		nint num = OpenInputDesktop(0u, inherit: false, 1u);
		if (num == IntPtr.Zero)
		{
			return true;
		}
		CloseDesktop(num);
		return false;
	}

	private bool ForegroundElevated(uint pid)
	{
		if (_elevatedByPid.TryGetValue(pid, out var value))
		{
			return value;
		}
		if (_elevatedByPid.Count >= 256)
		{
			_elevatedByPid.Clear();
		}
		value = false;
		if (_ownIntegrityRid >= 0)
		{
			nint num = OpenProcess(4096, inherit: false, pid);
			if (num != IntPtr.Zero)
			{
				try
				{
					value = IntegrityRid(num) > _ownIntegrityRid;
				}
				finally
				{
					CloseHandle(num);
				}
			}
		}
		_elevatedByPid[pid] = value;
		return value;
	}

	private void OnDesktopSwitch(nint hook, uint evt, nint hwnd, int obj, int child, uint thread, uint time)
	{
		Interlocked.Exchange(ref _desktopSwitched, 1);
	}

	private void ReconcileButtons(long qpc)
	{
		int num = Volatile.Read(in _held);
		if (num == 0 || IsBlind())
		{
			return;
		}
		bool swapped = SwapButtons();
		bool flag = false;
		for (int num2 = num; num2 != 0; num2 >>= 3)
		{
			MouseButton button = (MouseButton)(num2 & 7);
			if (!KeyHeld(ButtonVk(button, swapped)))
			{
				num = HeldRemove(num, button);
				_frameState?.UpdateMouseButton(ButtonName(button), isDown: false);
				flag = true;
			}
		}
		if (flag)
		{
			Volatile.Write(ref _held, num);
			Volatile.Write(ref _heldSinceQpc, (num == 0) ? 0 : qpc);
			long num3 = RecordingClock.Shared.WallMsFromQpc(qpc);
			InputStateSnapshot inputStateSnapshot = SnapshotHeldState();
			if ((object)inputStateSnapshot != null)
			{
				Log(inputStateSnapshot.ToRow("lost_up", num3), num3);
			}
		}
	}

	private void ReconcileButtonsNudge()
	{
		try
		{
			ReconcileButtons(Stopwatch.GetTimestamp());
		}
		catch (Exception ex)
		{
			NoteCallbackFailure("reconcile_buttons", ex);
		}
	}

	private void HoldButton(MouseButton button, long qpc)
	{
		Volatile.Write(ref _held, HeldAdd(Volatile.Read(in _held), button));
		Volatile.Write(ref _heldSinceQpc, qpc);
	}

	private void ReleaseButton(MouseButton button, long qpc)
	{
		int num = HeldRemove(Volatile.Read(in _held), button);
		Volatile.Write(ref _held, num);
		Volatile.Write(ref _heldSinceQpc, (num == 0) ? 0 : qpc);
	}

	private static long IntegrityRid(nint process)
	{
		if (!OpenProcessToken(process, 8u, out var token))
		{
			return -1L;
		}
		try
		{
			GetTokenInformation(token, 25, IntPtr.Zero, 0u, out var returnLength);
			nint num = Marshal.AllocHGlobal((int)returnLength);
			try
			{
				if (!GetTokenInformation(token, 25, num, returnLength, out var _))
				{
					return -1L;
				}
				nint sid = Marshal.ReadIntPtr(num);
				byte b = Marshal.ReadByte(GetSidSubAuthorityCount(sid));
				return (uint)Marshal.ReadInt32(GetSidSubAuthority(sid, (uint)(b - 1)));
			}
			finally
			{
				Marshal.FreeHGlobal(num);
			}
		}
		finally
		{
			CloseHandle(token);
		}
	}

	private nint MouseHookProc(int nCode, nint wParam, nint lParam)
	{
		if (nCode >= 0)
		{
			try
			{
				HandleMouseMessage(wParam, lParam);
			}
			catch (Exception ex)
			{
				NoteCallbackFailure("mouse_hook", ex);
			}
		}
		return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
	}

	private void HandleMouseMessage(nint wParam, nint lParam)
	{
		long timestamp = Stopwatch.GetTimestamp();
		long num = RecordingClock.Shared.WallMsFromQpc(timestamp);
		NoteDelivery(0, timestamp);
		MSLLHOOKSTRUCT s = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
		int num2 = (int)wParam;
		int num3 = MonitorTable.Current.Locate(s.pt.x, s.pt.y, out var clampedX, out var clampedY);
		AppContext app = Volatile.Read(in _app);
		bool flag = (s.flags & 1) != 0;
		int sourcePid = (flag ? (-1) : 0);
		RowFlags flags = ProvenanceFlags(flag, (s.flags & 2) != 0, (ulong)s.extra);
		_inputFacts.NoteMouse(flag, s.extra);
		_frameState?.UpdateMousePosition(clampedX, clampedY, num3, num);
		if (num2 == 512)
		{
			ReconcileButtons(timestamp);
		}
		int num4 = Volatile.Read(in _held);
		bool flag2 = num4 != 0;
		if (flag2)
		{
			Volatile.Write(ref _heldSinceQpc, timestamp);
		}
		OnUserInteraction?.Invoke((num2 != 512 || flag2) ? InputKind.Pointer : InputKind.Move);
		switch (num2)
		{
		case 512:
			if (flag2)
			{
				if (num - _lastDragMs >= AppConfig.MouseThrottleMs)
				{
					_lastDragMs = num;
					EventRow row = new EventRow
					{
						Kind = RowKind.Drag,
						Flags = flags,
						WallMs = num,
						OsTime = s.time,
						SourcePid = sourcePid,
						ExtraInfo = (ulong)s.extra,
						App = app,
						X = clampedX,
						Y = clampedY,
						Screen = num3,
						Button = HeldFirst(num4),
						Modifiers = ModifierMask(Volatile.Read(in _modifierState))
					};
					Enqueue(in row);
				}
				else if (flag)
				{
					EnqueueInjectedMove(num, s.time);
				}
			}
			else if (num - _lastMouseMoveMs >= AppConfig.MouseThrottleMs)
			{
				_lastMouseMoveMs = num;
				EventRow row = new EventRow
				{
					Kind = RowKind.MouseMove,
					Flags = flags,
					WallMs = num,
					OsTime = s.time,
					SourcePid = sourcePid,
					ExtraInfo = (ulong)s.extra,
					App = app,
					X = clampedX,
					Y = clampedY,
					Screen = num3
				};
				Enqueue(in row);
			}
			else if (flag)
			{
				EnqueueInjectedMove(num, s.time);
			}
			if (_lastMouseScreenIndex >= 0 && num3 != _lastMouseScreenIndex)
			{
				EventRow row = new EventRow
				{
					Kind = RowKind.ScreenTransition,
					WallMs = num,
					OsTime = s.time,
					FromScreen = _lastMouseScreenIndex,
					ToScreen = num3
				};
				Enqueue(in row);
			}
			_lastMouseScreenIndex = num3;
			break;
		case 513:
			Click(in s, timestamp, num, flags, sourcePid, app, clampedX, clampedY, num3, MouseButton.Left, isDown: true);
			break;
		case 514:
			Click(in s, timestamp, num, flags, sourcePid, app, clampedX, clampedY, num3, MouseButton.Left, isDown: false);
			break;
		case 516:
			Click(in s, timestamp, num, flags, sourcePid, app, clampedX, clampedY, num3, MouseButton.Right, isDown: true);
			break;
		case 517:
			Click(in s, timestamp, num, flags, sourcePid, app, clampedX, clampedY, num3, MouseButton.Right, isDown: false);
			break;
		case 519:
			Click(in s, timestamp, num, flags, sourcePid, app, clampedX, clampedY, num3, MouseButton.Middle, isDown: true);
			break;
		case 520:
			Click(in s, timestamp, num, flags, sourcePid, app, clampedX, clampedY, num3, MouseButton.Middle, isDown: false);
			break;
		case 523:
		case 524:
		{
			int num7 = (int)((s.mouseData >> 16) & 0xFFFF);
			if ((uint)(num7 - 1) <= 1u)
			{
				Click(in s, timestamp, num, flags, sourcePid, app, clampedX, clampedY, num3, (num7 == 1) ? MouseButton.Other3 : MouseButton.Other4, num2 == 523);
			}
			break;
		}
		case 522:
		{
			short num6 = (short)((s.mouseData >> 16) & 0xFFFF);
			Wheel(in s, timestamp, num, flags, sourcePid, flag, app, clampedX, clampedY, num3, 0.0, (double)num6 / 120.0, num6);
			break;
		}
		case 526:
		{
			short num5 = (short)((s.mouseData >> 16) & 0xFFFF);
			Wheel(in s, timestamp, num, flags, sourcePid, flag, app, clampedX, clampedY, num3, (double)(-num5) / 120.0, 0.0, num5);
			break;
		}
		case 515:
		case 518:
		case 521:
		case 525:
			break;
		}
	}

	private static RowFlags ProvenanceFlags(bool injected, bool lowerIl, ulong extraInfo)
	{
		RowFlags rowFlags = RowFlags.None;
		if (injected)
		{
			rowFlags |= RowFlags.Injected | RowFlags.HasExtraInfo;
		}
		if (lowerIl)
		{
			rowFlags |= RowFlags.LowerIl;
		}
		if ((extraInfo & 0xFFFFFF00u) == 4283520768u)
		{
			rowFlags |= RowFlags.HasExtraInfo;
		}
		return rowFlags;
	}

	private void Click(in MSLLHOOKSTRUCT s, long qpc, long now, RowFlags flags, int sourcePid, AppContext app, int x, int y, int screenIndex, MouseButton button, bool isDown)
	{
		if (isDown)
		{
			HoldButton(button, qpc);
		}
		else
		{
			ReleaseButton(button, qpc);
		}
		string button2 = ButtonName(button);
		_frameState?.UpdateMouseButton(button2, isDown);
		_frameState?.AppendClick(button2, isDown, x, y, screenIndex, now);
		nint root = RootWindowAt(x, y);
		AppContext appContext = TargetFor(root, app, screenIndex);
		if (appContext != null)
		{
			flags |= RowFlags.HasTarget;
		}
		Enqueue(new EventRow
		{
			Kind = RowKind.Click,
			Flags = (isDown ? (flags | RowFlags.IsDown) : flags),
			WallMs = now,
			OsTime = s.time,
			SourcePid = sourcePid,
			ExtraInfo = (ulong)s.extra,
			App = app,
			Target = appContext,
			X = x,
			Y = y,
			Screen = screenIndex,
			Button = button,
			ClickCount = (isDown ? ChainClick(button, now, x, y, root, screenIndex) : Math.Max(1, _clickChains[(uint)button].Count)),
			Modifiers = ModifierMask(Volatile.Read(in _modifierState))
		});
	}

	private int ChainClick(MouseButton button, long now, int x, int y, nint root, int screenIndex)
	{
		ref ClickChain reference = ref _clickChains[(uint)button];
		IReadOnlyList<MonitorTable.Entry> screens = MonitorTable.Current.Screens;
		uint num = ((screens.Count > screenIndex) ? screens[screenIndex].Dpi : 96u);
		InputSettingsSnapshot? inputSettingsSnapshot = _touchpad?.Settings;
		int num2 = inputSettingsSnapshot?.DoubleClickMs ?? 500;
		int num3 = (int)((inputSettingsSnapshot?.DoubleClickWidth ?? 4) * num / 96) / 2;
		int num4 = (int)((inputSettingsSnapshot?.DoubleClickHeight ?? 4) * num / 96) / 2;
		bool flag = reference.Count > 0 && now - reference.DownMs <= num2 && Math.Abs(x - reference.X) <= num3 && Math.Abs(y - reference.Y) <= num4 && root == reference.Root;
		reference.Count = ((!flag) ? 1 : (reference.Count + 1));
		reference.DownMs = now;
		reference.X = x;
		reference.Y = y;
		reference.Root = root;
		return reference.Count;
	}

	private void Wheel(in MSLLHOOKSTRUCT s, long qpc, long now, RowFlags flags, int sourcePid, bool injected, AppContext app, int x, int y, int screenIndex, double dx, double dy, short delta)
	{
		TouchpadChannel touchpad = _touchpad;
		bool flag = false;
		if (delta % 120 != 0)
		{
			if (touchpad != null && touchpad.IsOsCopy(qpc, ref _lastOsWheelQpc))
			{
				flag = true;
			}
			else if (injected && !IsBlind())
			{
				touchpad?.NoteOrphanWheel();
			}
		}
		if (!flag)
		{
			_frameState?.AppendScroll(dx, dy, screenIndex, now);
		}
		AppContext appContext = TargetFor(RootWindowAt(x, y), app, screenIndex);
		if (appContext != null)
		{
			flags |= RowFlags.HasTarget;
		}
		Enqueue(new EventRow
		{
			Kind = (flag ? RowKind.ScrollOs : RowKind.Scroll),
			Flags = flags,
			WallMs = now,
			OsTime = s.time,
			SourcePid = (flag ? (-1) : sourcePid),
			ExtraInfo = (ulong)s.extra,
			App = app,
			Target = appContext,
			X = x,
			Y = y,
			Screen = screenIndex,
			DeltaX = dx,
			DeltaY = dy,
			Channel = (flag ? ScrollChannel.OsGesture : ScrollChannel.Wheel),
			GestureId = (flag ? touchpad.CurrentGestureId : 0),
			Modifiers = ModifierMask(Volatile.Read(in _modifierState))
		});
	}

	private nint RootWindowAt(int x, int y)
	{
		nint num = ChildWindowFromPointEx(_desktopWindow, new POINT
		{
			x = x,
			y = y
		}, 7u);
		if (num == IntPtr.Zero || num == _desktopWindow)
		{
			return IntPtr.Zero;
		}
		return GetAncestor(num, 2u);
	}

	private AppContext? TargetFor(nint root, AppContext app, int screenIndex)
	{
		if (root == IntPtr.Zero || ((IntPtr)root).ToInt64() == app.Hwnd || IsCloaked(root))
		{
			return null;
		}
		GetWindowThreadProcessId(root, out var pid);
		if (_targets.TryGetValue(root, out AppContext value) && value.Pid == (int)pid)
		{
			return value;
		}
		if (_targets.Count >= 256)
		{
			_targets.Clear();
		}
		(string Name, string BundleId, string? ExePath) appInfo = GetAppInfo(pid, null, root, ProcessSighting.Foreground);
		string item = appInfo.Name;
		string item2 = appInfo.BundleId;
		value = new AppContext(item, item2, screenIndex, (int)pid, ((IntPtr)root).ToInt64(), UiaGuard.ClassNameOf(root), null);
		_targets[root] = value;
		return value;
	}

	private static bool IsCloaked(nint hwnd)
	{
		if (DwmGetWindowAttribute(hwnd, 14, out int pvAttribute, 4) == 0)
		{
			return pvAttribute != 0;
		}
		return false;
	}

	private void EnqueueInjectedMove(long now, uint osTime)
	{
		EventRow row = new EventRow
		{
			Kind = RowKind.InjectedMove,
			Flags = RowFlags.Injected,
			WallMs = now,
			OsTime = osTime
		};
		Enqueue(in row);
	}

	private void Enqueue(in EventRow row)
	{
		Volatile.Read(in _eventSink)?.Enqueue(in row);
	}

	private void OnRawInput(nint hRawInput)
	{
		try
		{
			HandleRawInput(hRawInput);
		}
		catch (Exception ex)
		{
			NoteCallbackFailure("raw_input", ex);
		}
	}

	private void HandleRawInput(nint hRawInput)
	{
		if (_rawInputBuffer == IntPtr.Zero)
		{
			return;
		}
		long timestamp = Stopwatch.GetTimestamp();
		if (!HidInterop.TryReadRawInput(hRawInput, ref _rawInputBuffer, ref _rawInputBufferSize, out var _))
		{
			_inputFacts.NoteRawFailure();
			return;
		}
		HidInterop.RAWINPUTHEADER rAWINPUTHEADER = Marshal.PtrToStructure<HidInterop.RAWINPUTHEADER>(_rawInputBuffer);
		if (rAWINPUTHEADER.dwType == 1)
		{
			_inputFacts.NoteRawKeyboard();
			NoteWitness(1, timestamp);
		}
		else
		{
			if (rAWINPUTHEADER.dwType != 0)
			{
				return;
			}
			NoteWitness(0, timestamp);
			RAWINPUT rAWINPUT = Marshal.PtrToStructure<RAWINPUT>(_rawInputBuffer);
			bool flag = (rAWINPUT.mouse.usFlags & 1) != 0;
			_inputFacts.NoteRawPacket(rAWINPUT.header.hDevice, flag);
			if (!flag)
			{
				int lLastX = rAWINPUT.mouse.lLastX;
				int lLastY = rAWINPUT.mouse.lLastY;
				if ((lLastX != 0 || lLastY != 0) && _mouseDeltaFlushMs != 0)
				{
					EventRow row = new EventRow
					{
						Kind = RowKind.RawDelta,
						WallMs = RecordingClock.Shared.WallMsFromQpc(timestamp),
						OsTime = (uint)GetMessageTime(),
						App = Volatile.Read(in _app),
						Screen = _lastMouseScreenIndex,
						Dx = lLastX,
						Dy = lLastY
					};
					Enqueue(in row);
				}
			}
		}
	}

	private void OnDeviceChange(nint change, nint hDevice)
	{
		if ((int)change == 2)
		{
			_inputFacts.ForgetDevice(hDevice);
		}
	}

	private nint KeyHookProc(int nCode, nint wParam, nint lParam)
	{
		if (nCode >= 0)
		{
			try
			{
				HandleKeyMessage(wParam, lParam);
			}
			catch (Exception ex)
			{
				NoteCallbackFailure("key_hook", ex);
			}
		}
		return CallNextHookEx(_keyHook, nCode, wParam, lParam);
	}

	private void HandleKeyMessage(nint wParam, nint lParam)
	{
		long timestamp = Stopwatch.GetTimestamp();
		long num = RecordingClock.Shared.WallMsFromQpc(timestamp);
		NoteDelivery(1, timestamp);
		KBDLLHOOKSTRUCT kBDLLHOOKSTRUCT = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
		bool flag = (kBDLLHOOKSTRUCT.flags & 0x10) != 0;
		_inputFacts.NoteKey(flag, kBDLLHOOKSTRUCT.extra);
		OnUserInteraction?.Invoke(InputKind.Key);
		int num2 = (int)wParam;
		bool flag2 = ((num2 == 256 || num2 == 260) ? true : false);
		bool flag3 = flag2;
		int num3 = ModifierBit(kBDLLHOOKSTRUCT.vkCode);
		bool flag4 = num3 != 0;
		bool flag5 = false;
		if (!flag4)
		{
			lock (_pressedKeys)
			{
				if (flag3)
				{
					flag5 = !_pressedKeys.Add(kBDLLHOOKSTRUCT.vkCode);
				}
				else
				{
					_pressedKeys.Remove(kBDLLHOOKSTRUCT.vkCode);
				}
			}
		}
		int num4 = Volatile.Read(in _modifierState);
		num4 = (((GetKeyState(20) & 1) != 0) ? (num4 | 0x100) : (num4 & -257));
		if (flag4)
		{
			num4 = (flag3 ? (num4 | num3) : (num4 & ~num3));
		}
		else if (((kBDLLHOOKSTRUCT.vkCode == 20) & flag3) && !flag5)
		{
			num4 ^= 0x100;
		}
		Volatile.Write(ref _modifierState, num4);
		uint num5 = ModifierMask(num4);
		nint foregroundWindow = GetForegroundWindow();
		nint hkl = ((foregroundWindow != IntPtr.Zero) ? GetKeyboardLayout(GetWindowThreadProcessId(foregroundWindow, out var _)) : _lastHkl);
		NoteKeyboardLayout(hkl, num);
		_frameState?.UpdateModifiers(num5);
		if (SuppressKeys)
		{
			if (!flag4 && !flag3)
			{
				_frameState?.UpdatePressedKey(kBDLLHOOKSTRUCT.vkCode, isDown: false);
			}
			return;
		}
		AppContext appContext = Volatile.Read(in _app);
		RowFlags rowFlags = ((MonitorTable.Current.Screens.Count > 1) ? RowFlags.HasDisplayIndex : RowFlags.None);
		rowFlags |= ProvenanceFlags(flag, (kBDLLHOOKSTRUCT.flags & 2) != 0, (ulong)kBDLLHOOKSTRUCT.extra);
		if ((kBDLLHOOKSTRUCT.flags & 1) != 0)
		{
			rowFlags |= RowFlags.Extended;
		}
		int sourcePid = (flag ? (-1) : 0);
		if (!flag4)
		{
			string chars = TranslateKey(kBDLLHOOKSTRUCT.vkCode, kBDLLHOOKSTRUCT.scanCode, hkl, flag3, out var dead);
			if (dead)
			{
				rowFlags |= RowFlags.Dead;
			}
			if (!flag5)
			{
				_frameState?.UpdatePressedKey(kBDLLHOOKSTRUCT.vkCode, flag3);
				_frameState?.AppendKeyTransition(kBDLLHOOKSTRUCT.vkCode, chars, flag3, num, ((rowFlags & RowFlags.HasDisplayIndex) != 0) ? new int?(appContext.DisplayIndex) : ((int?)null));
			}
			if (flag3)
			{
				rowFlags |= RowFlags.IsDown;
			}
			if (flag5)
			{
				rowFlags |= RowFlags.IsRepeat;
			}
			Enqueue(new EventRow
			{
				Kind = RowKind.Key,
				Flags = rowFlags,
				WallMs = num,
				OsTime = kBDLLHOOKSTRUCT.time,
				SourcePid = sourcePid,
				ExtraInfo = (ulong)kBDLLHOOKSTRUCT.extra,
				App = appContext,
				Display = appContext.DisplayIndex,
				Modifiers = num5,
				VkCode = kBDLLHOOKSTRUCT.vkCode,
				ScanCode = kBDLLHOOKSTRUCT.scanCode,
				Hkl = ((IntPtr)hkl).ToInt64(),
				Chars = chars
			});
		}
		if (num5 != Volatile.Read(in _lastEmittedModifiers))
		{
			Volatile.Write(ref _lastEmittedModifiers, num5);
			EventRow row = new EventRow
			{
				Kind = RowKind.ModifierChange,
				Flags = (rowFlags & (RowFlags.Injected | RowFlags.LowerIl | RowFlags.HasDisplayIndex | RowFlags.HasExtraInfo)),
				WallMs = num,
				OsTime = kBDLLHOOKSTRUCT.time,
				SourcePid = sourcePid,
				ExtraInfo = (ulong)kBDLLHOOKSTRUCT.extra,
				Display = appContext.DisplayIndex,
				Modifiers = num5
			};
			Enqueue(in row);
		}
	}

	private static int ModifierBit(uint vkCode)
	{
		switch (vkCode)
		{
		case 16u:
		case 160u:
			return 1;
		case 161u:
			return 2;
		case 17u:
		case 162u:
			return 4;
		case 163u:
			return 8;
		case 18u:
		case 164u:
			return 16;
		case 165u:
			return 32;
		case 91u:
			return 64;
		case 92u:
			return 128;
		default:
			return 0;
		}
	}

	private static uint ModifierMask(int state)
	{
		uint num = 0u;
		if ((state & 3) != 0)
		{
			num |= 1;
		}
		if ((state & 0xC) != 0)
		{
			num |= 2;
		}
		if ((state & 0x30) != 0)
		{
			num |= 4;
		}
		if ((state & 0xC0) != 0)
		{
			num |= 8;
		}
		if ((state & 0x100) != 0)
		{
			num |= 0x10;
		}
		return num;
	}

	private string TranslateKey(uint vkCode, uint scanCode, nint hkl, bool isDown, out bool dead)
	{
		dead = false;
		if (vkCode == 231)
		{
			char c = (char)scanCode;
			if (char.IsHighSurrogate(c))
			{
				if (isDown)
				{
					_pendingHighSurrogate = c;
				}
				return "";
			}
			if (char.IsLowSurrogate(c))
			{
				if (!isDown || _pendingHighSurrogate == '\0')
				{
					return "";
				}
				string result = new string(stackalloc char[2] { _pendingHighSurrogate, c });
				_pendingHighSurrogate = '\0';
				return result;
			}
			return c.ToString();
		}
		int num = Volatile.Read(in _modifierState);
		Array.Clear(_keyState);
		if ((num & 1) != 0)
		{
			_keyState[160] = 128;
		}
		if ((num & 2) != 0)
		{
			_keyState[161] = 128;
		}
		if ((num & 3) != 0)
		{
			_keyState[16] = 128;
		}
		if ((num & 4) != 0)
		{
			_keyState[162] = 128;
		}
		if ((num & 8) != 0)
		{
			_keyState[163] = 128;
		}
		if ((num & 0xC) != 0)
		{
			_keyState[17] = 128;
		}
		if ((num & 0x10) != 0)
		{
			_keyState[164] = 128;
		}
		if ((num & 0x20) != 0)
		{
			_keyState[165] = 128;
		}
		if ((num & 0x30) != 0)
		{
			_keyState[18] = 128;
		}
		if ((num & 0x100) != 0)
		{
			_keyState[20] = 1;
		}
		int num2 = ToUnicodeEx(vkCode, scanCode, _keyState, _keyChars, _keyChars.Length, 4u, hkl);
		if (num2 < 0)
		{
			dead = true;
			if (isDown)
			{
				_pendingDeadKey = _keyChars[0];
			}
			return _keyChars[0].ToString();
		}
		if (num2 == 0)
		{
			return "";
		}
		string text = new string(_keyChars, 0, num2);
		if (!isDown || _pendingDeadKey == '\0')
		{
			return text;
		}
		char pendingDeadKey = _pendingDeadKey;
		_pendingDeadKey = '\0';
		if (text == " ")
		{
			return pendingDeadKey.ToString();
		}
		if (CombiningMarks.TryGetValue(pendingDeadKey, out var value))
		{
			string text2 = (text + value).Normalize(NormalizationForm.FormC);
			if (text2.Length == text.Length)
			{
				return text2;
			}
		}
		return pendingDeadKey + text;
	}

	private void NoteKeyboardLayout(nint hkl, long now)
	{
		if (hkl != IntPtr.Zero && hkl != _lastHkl)
		{
			_lastHkl = hkl;
			_inputSourceRequests.Enqueue((((IntPtr)hkl).ToInt64(), now));
			_appNameWake.Set();
		}
	}

	private void ClearKeyboardStateForPrivacy()
	{
		lock (_pressedKeys)
		{
			_pressedKeys.Clear();
		}
		_frameState?.ClearKeyboardState();
	}

	private void OnWinEvent(nint hook, uint evt, nint hwnd, int obj, int child, uint thread, uint time)
	{
		try
		{
			if (hwnd == IntPtr.Zero)
			{
				ScheduleForegroundRecheck();
				return;
			}
			long now = RecordingClock.Shared.WallMsFromQpc(Stopwatch.GetTimestamp());
			PrimeWindowRectState(hwnd, now);
			LogActiveApp(hwnd, now);
			ScheduleForegroundRecheck();
		}
		catch (Exception ex)
		{
			NoteCallbackFailure("foreground_winevent", ex);
		}
	}

	private void ScheduleForegroundRecheck()
	{
		nint num = Volatile.Read(in _inputWindowHandle);
		if (num != IntPtr.Zero)
		{
			SetTimer(num, 2u, 250u, IntPtr.Zero);
		}
	}

	private void OnForegroundRecheck()
	{
		try
		{
			KillTimer(Volatile.Read(in _inputWindowHandle), 2u);
			CompareForeground(recheck: true);
		}
		catch (Exception ex)
		{
			NoteCallbackFailure("foreground_recheck", ex);
		}
	}

	private void SetForegroundCompare(bool on)
	{
		nint num = Volatile.Read(in _inputWindowHandle);
		if (num != IntPtr.Zero && on != _foregroundCompareArmed)
		{
			_foregroundCompareArmed = on;
			if (on)
			{
				SetTimer(num, 3u, 2000u, IntPtr.Zero);
			}
			else
			{
				KillTimer(num, 3u);
			}
		}
	}

	private void OnForegroundCompare()
	{
		try
		{
			if (Volatile.Read(in _eventSink) != null)
			{
				CompareForeground(recheck: false);
			}
		}
		catch (Exception ex)
		{
			NoteCallbackFailure("foreground_compare", ex);
		}
	}

	private void CompareForeground(bool recheck)
	{
		nint foregroundWindow = GetForegroundWindow();
		if (foregroundWindow != IntPtr.Zero && (foregroundWindow != _lastForegroundHwnd || (recheck && _hostedAppPending && FindCoreWindow(foregroundWindow) != IntPtr.Zero)))
		{
			long now = RecordingClock.Shared.WallMsFromQpc(Stopwatch.GetTimestamp());
			PrimeWindowRectState(foregroundWindow, now);
			LogActiveApp(foregroundWindow, now);
		}
	}

	private void OnVisibilityChanged(nint hook, uint evt, nint hwnd, int obj, int child, uint thread, uint time)
	{
		try
		{
			if (hwnd == IntPtr.Zero || obj != 0 || child != 0)
			{
				return;
			}
			if (evt == 32771)
			{
				_windowRects.Remove(hwnd);
			}
			else if (_windowRects.Count > 512)
			{
				foreach (nint item3 in _windowRects.Keys.Where((nint h) => !IsWindow(h)).ToList())
				{
					_windowRects.Remove(item3);
				}
			}
			if (!IsTopLevelAppWindow(hwnd))
			{
				return;
			}
			ScheduleForegroundRecheck();
			GetWindowThreadProcessId(hwnd, out var pid);
			if (pid == 0)
			{
				return;
			}
			HashSet<nint> hashSet = VisibleWindowsOf(pid);
			hashSet.Remove(hwnd);
			bool flag = hashSet.Count == 0;
			if (evt == 32770)
			{
				hashSet.Add(hwnd);
			}
			if (flag)
			{
				(string Name, string BundleId, string? ExePath) appInfo = GetAppInfo(pid, null, hwnd, ProcessSighting.Window);
				string item = appInfo.Name;
				string item2 = appInfo.BundleId;
				long num = NowMs();
				switch (evt)
				{
				case 32771u:
					Log(new
					{
						type = "app_hidden",
						appName = item,
						bundleId = item2,
						timestamp = num
					}, num);
					break;
				case 32770u:
					Log(new
					{
						type = "app_unhidden",
						appName = item,
						bundleId = item2,
						timestamp = num
					}, num);
					break;
				}
			}
		}
		catch (Exception ex)
		{
			NoteCallbackFailure("visibility_winevent", ex);
		}
	}

	private static bool IsTopLevelAppWindow(nint hwnd)
	{
		if (((ulong)GetWindowLongPtr(hwnd, -20) & 0x80uL) == 0L)
		{
			return GetWindow(hwnd, 4u) == IntPtr.Zero;
		}
		return false;
	}

	private HashSet<nint> VisibleWindowsOf(uint pid)
	{
		if (_visibleByPid.TryGetValue(pid, out HashSet<nint> windows))
		{
			windows.RemoveWhere((nint h) => !IsWindow(h) || !IsWindowVisible(h));
			return windows;
		}
		if (_visibleByPid.Count >= 256)
		{
			_visibleByPid.Clear();
		}
		windows = new HashSet<nint>();
		EnumWindowsProc enumWindowsProc = (nint h, nint _) =>
		{
			GetWindowThreadProcessId(h, out var pid2);
			if (pid2 == pid && IsWindowVisible(h) && IsTopLevelAppWindow(h))
			{
				windows.Add(h);
			}
			return true;
		};
		EnumWindows(enumWindowsProc, IntPtr.Zero);
		GC.KeepAlive(enumWindowsProc);
		_visibleByPid[pid] = windows;
		return windows;
	}

	private void OnLocationChanged(nint hook, uint evt, nint hwnd, int obj, int child, uint thread, uint time)
	{
		try
		{
			HandleLocationChanged(hwnd, obj, child);
		}
		catch (Exception ex)
		{
			NoteCallbackFailure("location_winevent", ex);
		}
	}

	private void HandleLocationChanged(nint hwnd, int obj, int child)
	{
		if (hwnd == IntPtr.Zero || obj != 0 || child != 0 || !IsWindowVisible(hwnd) || IsIconic(hwnd) || !IsTopLevelAppWindow(hwnd) || !TryGetWindowRect(hwnd, out var rect))
		{
			return;
		}
		int left = rect.Left;
		int top = rect.Top;
		int num = rect.Right - rect.Left;
		int num2 = rect.Bottom - rect.Top;
		if (num <= 0 || num2 <= 0)
		{
			return;
		}
		long num3 = NowMs();
		if (!_windowRects.TryGetValue(hwnd, out var value))
		{
			_windowRects[hwnd] = new WindowRectState
			{
				X = left,
				Y = top,
				Width = num,
				Height = num2,
				FirstSeenMs = num3,
				LastEmitMs = num3
			};
			return;
		}
		bool flag = left != value.X || top != value.Y;
		bool flag2 = num != value.Width || num2 != value.Height;
		if (!flag && !flag2)
		{
			return;
		}
		WindowRectState value2 = value;
		value2.X = left;
		value2.Y = top;
		value2.Width = num;
		value2.Height = num2;
		_windowRects[hwnd] = value2;
		if (num3 - value.FirstSeenMs >= 800 && num3 - value.LastEmitMs >= 250)
		{
			value2.LastEmitMs = num3;
			_windowRects[hwnd] = value2;
			StringBuilder stringBuilder = new StringBuilder(512);
			GetWindowText(hwnd, stringBuilder, 512);
			string windowTitle = stringBuilder.ToString();
			GetWindowThreadProcessId(hwnd, out var pid);
			var (appName, bundleId, _) = GetAppInfo(pid, windowTitle, hwnd, ProcessSighting.Window);
			if (flag)
			{
				Log(new
				{
					type = "window_moved",
					x = left,
					y = top,
					width = num,
					height = num2,
					appName = appName,
					bundleId = bundleId,
					windowTitle = windowTitle,
					timestamp = num3
				}, num3);
			}
			if (flag2)
			{
				Log(new
				{
					type = "window_resized",
					x = left,
					y = top,
					width = num,
					height = num2,
					appName = appName,
					bundleId = bundleId,
					windowTitle = windowTitle,
					timestamp = num3
				}, num3);
			}
		}
	}

	private void OnWindowStateEvent(nint hook, uint evt, nint hwnd, int obj, int child, uint thread, uint time)
	{
		try
		{
			if (hwnd != IntPtr.Zero && obj == 0 && child == 0 && IsTopLevelAppWindow(hwnd))
			{
				GetWindowThreadProcessId(hwnd, out var pid);
				string item = GetAppInfo(pid, null, hwnd, ProcessSighting.Window).Name;
				long num = NowMs();
				long hwnd2 = ((IntPtr)hwnd).ToInt64();
				switch (evt)
				{
				case 22u:
					Log(new
					{
						type = "window_minimized",
						hwnd = hwnd2,
						appName = item,
						timestamp = num
					}, num);
					break;
				case 23u:
					Log(new
					{
						type = "window_restored",
						hwnd = hwnd2,
						appName = item,
						timestamp = num
					}, num);
					break;
				case 32791u:
				case 32792u:
				{
					int reason = ((DwmGetWindowAttribute(hwnd, 14, out int pvAttribute, 4) == 0) ? pvAttribute : 0);
					Log(new
					{
						type = ((evt == 32791) ? "window_cloaked" : "window_uncloaked"),
						hwnd = hwnd2,
						appName = item,
						reason = reason,
						timestamp = num
					}, num);
					break;
				}
				}
			}
		}
		catch (Exception ex)
		{
			NoteCallbackFailure("window_state_winevent", ex);
		}
	}

	private void OnImeEvent(nint hook, uint evt, nint hwnd, int obj, int child, uint thread, uint time)
	{
		try
		{
			bool flag = evt == 32807;
			if (flag != _imeShown)
			{
				_imeShown = flag;
				long num = NowMs();
				Log(new
				{
					type = "ime_composition",
					state = (flag ? "begin" : "end"),
					timestamp = num
				}, num);
			}
		}
		catch (Exception ex)
		{
			NoteCallbackFailure("ime_winevent", ex);
		}
	}

	private void PrimeWindowRectState(nint hwnd, long now)
	{
		if (TryGetWindowRect(hwnd, out var rect))
		{
			int num = rect.Right - rect.Left;
			int num2 = rect.Bottom - rect.Top;
			if (num > 0 && num2 > 0)
			{
				_windowRects[hwnd] = new WindowRectState
				{
					X = rect.Left,
					Y = rect.Top,
					Width = num,
					Height = num2,
					FirstSeenMs = now,
					LastEmitMs = now
				};
			}
		}
	}

	private static bool TryGetWindowRect(nint hwnd, out RECT rect)
	{
		if (DwmGetWindowAttribute(hwnd, 9, out rect, Marshal.SizeOf<RECT>()) == 0)
		{
			return true;
		}
		return GetWindowRect(hwnd, out rect);
	}

	private void PollWindowTitle()
	{
		try
		{
			nint foregroundWindow = GetForegroundWindow();
			if (foregroundWindow == IntPtr.Zero)
			{
				return;
			}
			AppContext appContext = Volatile.Read(in _app);
			if (((IntPtr)foregroundWindow).ToInt64() == appContext.Hwnd)
			{
				StringBuilder stringBuilder = new StringBuilder(512);
				GetWindowText(foregroundWindow, stringBuilder, 512);
				string text = stringBuilder.ToString();
				if (!string.IsNullOrEmpty(text) && !(text == Volatile.Read(in _activeApp)?.WindowTitle))
				{
					long num = NowMs();
					Log(new
					{
						type = "window_title_changed",
						title = text,
						appName = appContext.AppName,
						bundleId = appContext.BundleId,
						browserUrl = _lastBrowserUrl,
						documentPath = (string)null,
						timestamp = num
					}, num);
					ActiveAppSnapshot activeAppSnapshot = new ActiveAppSnapshot(appContext.AppName, appContext.BundleId, appContext.Pid, text, _lastBrowserUrl, IsWindowFullscreen(foregroundWindow));
					Volatile.Write(ref _activeApp, activeAppSnapshot);
					_frameState?.UpdateActiveApp(activeAppSnapshot);
					WindowTitleChanged?.Invoke(new ForegroundWindowInfo(appContext.BundleId, appContext.AppName, text, MonitorTable.Current.IndexOfWindow(foregroundWindow), _lastBrowserUrl, appContext.Pid, appContext.Hwnd));
					_titlePollFailureLogged = false;
				}
			}
		}
		catch (Exception ex)
		{
			if (!_titlePollFailureLogged)
			{
				_titlePollFailureLogged = true;
				KuroLog.SystemEvent("window title poll failed: " + ex.GetType().Name + ": " + ex.Message, "PollWindowTitle", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Input\\InputMonitor.cs", 2598);
			}
		}
	}

	private (ForegroundWindowInfo Info, ActiveAppSnapshot Window)? PublishForeground(nint hwnd, long now)
	{
		ForegroundWindowInfo foregroundWindowInfo = BuildForegroundWindowInfo(hwnd, out var identity, ProcessSighting.Foreground);
		if (foregroundWindowInfo == null)
		{
			return null;
		}
		ActiveAppSnapshot activeAppSnapshot = new ActiveAppSnapshot(foregroundWindowInfo.AppName, foregroundWindowInfo.ExeName, foregroundWindowInfo.Pid, foregroundWindowInfo.WindowTitle, foregroundWindowInfo.BrowserUrl, IsWindowFullscreen(hwnd));
		_lastBrowserUrl = foregroundWindowInfo.BrowserUrl;
		_foregroundPath = identity.Path;
		_hostedAppPending = identity.HostedPending;
		AppContext appContext = new AppContext(foregroundWindowInfo.AppName, foregroundWindowInfo.ExeName, foregroundWindowInfo.DisplayIndex, foregroundWindowInfo.Pid, ((IntPtr)hwnd).ToInt64(), UiaGuard.ClassNameOf(hwnd), identity.Aumid);
		Volatile.Write(ref _app, appContext);
		Volatile.Write(ref _foregroundElevated, ForegroundElevated(identity.Pid) ? appContext : null);
		Volatile.Write(ref _activeApp, activeAppSnapshot);
		_frameState?.UpdateActiveApp(activeAppSnapshot);
		NoteKeyboardLayout(GetKeyboardLayout(GetWindowThreadProcessId(hwnd, out var _)), now);
		return (foregroundWindowInfo, activeAppSnapshot);
	}

	private void LogActiveApp(nint hwnd, long now)
	{
		(ForegroundWindowInfo, ActiveAppSnapshot)? tuple = PublishForeground(hwnd, now);
		if (tuple.HasValue)
		{
			(ForegroundWindowInfo, ActiveAppSnapshot) valueOrDefault = tuple.GetValueOrDefault();
			ForegroundWindowInfo item = valueOrDefault.Item1;
			ActiveAppSnapshot item2 = valueOrDefault.Item2;
			_lastForegroundHwnd = hwnd;
			AppContext appContext = Volatile.Read(in _app);
			string exeName = item.ExeName;
			string appName = item.AppName;
			string windowTitle = item.WindowTitle;
			int displayIndex = item.DisplayIndex;
			string browserUrl = item.BrowserUrl;
			Log(new
			{
				type = "active_app",
				name = appName,
				bundleId = exeName,
				windowTitle = windowTitle,
				browserUrl = browserUrl,
				pid = item2.Pid,
				documentPath = (string)null,
				isFullScreen = (item2.IsFullScreen == true),
				hwnd = appContext.Hwnd,
				windowClass = appContext.WindowClass,
				aumid = appContext.Aumid,
				timestamp = now
			}, now);
			_lastSecureFieldFocused = null;
			ForegroundChanged?.Invoke(new ForegroundWindowInfo(exeName, appName, windowTitle, displayIndex, browserUrl, item.Pid, item.Hwnd));
			if (!BrowserCatalog.IsBrowserExe(exeName))
			{
				RequestSecureFieldProbe();
			}
		}
	}

	private void RequestSecureFieldProbe()
	{
		Interlocked.Exchange(ref _uiaForegroundProbePending, 1);
		_uiaWake.Set();
	}

	private WindowIdentity IdentifyWindow(nint hwnd, string windowTitle, ProcessSighting sighting)
	{
		GetWindowThreadProcessId(hwnd, out var pid);
		var (name, text, path) = GetAppInfo(pid, windowTitle, hwnd, sighting);
		if (!text.Equals("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase))
		{
			return new WindowIdentity(name, text, path, pid, null, HostedPending: false);
		}
		nint num = FindCoreWindow(hwnd);
		if (num == IntPtr.Zero)
		{
			return new WindowIdentity(name, text, path, pid, null, HostedPending: true);
		}
		GetWindowThreadProcessId(num, out var pid2);
		(string, string, string) appInfo = GetAppInfo(pid2, windowTitle, num, sighting);
		return new WindowIdentity(appInfo.Item1, appInfo.Item2, appInfo.Item3, pid2, AumidOf(pid2, sighting), HostedPending: false);
	}

	private static nint FindCoreWindow(nint frameHost)
	{
		nint found = IntPtr.Zero;
		EnumWindowsProc enumWindowsProc = (nint h, nint _) =>
		{
			if (UiaGuard.ClassNameOf(h) != "Windows.UI.Core.CoreWindow")
			{
				return true;
			}
			found = h;
			return false;
		};
		EnumChildWindows(frameHost, enumWindowsProc, IntPtr.Zero);
		GC.KeepAlive(enumWindowsProc);
		return found;
	}

	private string? AumidOf(uint pid, ProcessSighting sighting)
	{
		if (sighting == ProcessSighting.None || !_processes.TryGetValue(pid, out ProcessEntry value))
		{
			return null;
		}
		if (!value.AumidRead)
		{
			value.AumidRead = true;
			StringBuilder stringBuilder = new StringBuilder(256);
			uint length = (uint)stringBuilder.Capacity;
			value.Aumid = ((GetApplicationUserModelId(value.Handle, ref length, stringBuilder) == 0) ? stringBuilder.ToString() : null);
		}
		return value.Aumid;
	}

	private ForegroundWindowInfo? BuildForegroundWindowInfo(nint hwnd, out WindowIdentity identity, ProcessSighting sighting)
	{
		identity = default;
		if (hwnd == IntPtr.Zero)
		{
			return null;
		}
		StringBuilder stringBuilder = new StringBuilder(512);
		GetWindowText(hwnd, stringBuilder, 512);
		string windowTitle = stringBuilder.ToString();
		identity = IdentifyWindow(hwnd, windowTitle, sighting);
		int displayIndex = MonitorTable.Current.IndexOfWindow(hwnd);
		return new ForegroundWindowInfo(identity.Exe, identity.Name, windowTitle, displayIndex, null, (int)identity.Pid, ((IntPtr)hwnd).ToInt64());
	}

	private void OnClipboardChanged()
	{
		try
		{
			ClipboardMarker item = new ClipboardMarker(NowMs(), Volatile.Read(in _app));
			ThreadPool.UnsafeQueueUserWorkItem(((InputMonitor Monitor, ClipboardMarker Marker) state) =>
			{
				state.Monitor.ReadClipboard(state.Marker);
			}, (this, item), preferLocal: false);
		}
		catch (Exception ex)
		{
			NoteCallbackFailure("clipboard", ex);
		}
	}

	private void ReadClipboard(ClipboardMarker marker)
	{
		try
		{
			uint clipboardSequenceNumber = GetClipboardSequenceNumber();
			bool flag = IsClipboardFormatAvailable(13u) || IsClipboardFormatAvailable(1u);
			bool flag2 = IsClipboardFormatAvailable(2u) || IsClipboardFormatAvailable(8u) || IsClipboardFormatAvailable(17u);
			bool flag3 = IsClipboardFormatAvailable(15u);
			string contentType;
			if (flag)
			{
				contentType = "text";
			}
			else if (flag2)
			{
				contentType = "image";
			}
			else
			{
				contentType = (flag3 ? "file" : "other");
			}
			long length = 0L;
			if (OpenClipboard(IntPtr.Zero))
			{
				try
				{
					uint num = EnumClipboardFormats(0u);
					while (true)
					{
						string text;
						string text2;
						switch (num)
						{
						case 2u:
							if (num == 2)
							{
								goto case 8u;
							}
							goto default;
						case 1u:
						case 13u:
							text = "text";
							goto IL_00d2;
						case 8u:
						case 17u:
							text = "image";
							goto IL_00d2;
						case 15u:
							text = "file";
							goto IL_00d2;
						default:
							text = null;
							goto IL_00d2;
						case 0u:
							break;
							IL_00d2:
							text2 = text;
							if (text2 == null)
							{
								goto IL_00e0;
							}
							contentType = text2;
							break;
						}
						break;
						IL_00e0:
						num = EnumClipboardFormats(num);
					}
					if (flag)
					{
						length = ClipboardTextLength();
					}
				}
				finally
				{
					CloseClipboard();
				}
			}
			Log(new
			{
				type = "clipboard",
				contentType = contentType,
				length = length,
				appName = marker.App.AppName,
				bundleId = marker.App.BundleId,
				clipboardSeq = clipboardSequenceNumber,
				hasText = flag,
				hasImage = flag2,
				hasFiles = flag3,
				timestamp = marker.WallMs
			}, marker.WallMs);
		}
		catch (Exception ex)
		{
			NoteCallbackFailure("clipboard", ex);
		}
	}

	private static long ClipboardTextLength()
	{
		nint clipboardData = GetClipboardData(13u);
		if (clipboardData == IntPtr.Zero)
		{
			return 0L;
		}
		nint num = GlobalLock(clipboardData);
		if (num == IntPtr.Zero)
		{
			return 0L;
		}
		try
		{
			long num2 = (long)GlobalSize(clipboardData) / 2L;
			long num3;
			for (num3 = 0L; num3 < num2 && Marshal.ReadInt16(num, (int)(num3 * 2)) != 0; num3++)
			{
			}
			return num3;
		}
		finally
		{
			GlobalUnlock(clipboardData);
		}
	}

	private void UiaWorkerProc()
	{
		long num = AppConfig.TextSelectionPollMs;
		long num2 = Environment.TickCount64 + num;
		while (_uiaRunning)
		{
			int millisecondsTimeout = (int)Math.Clamp(num2 - Environment.TickCount64, 0L, num);
			_uiaWake.WaitOne(millisecondsTimeout);
			if (!_uiaRunning)
			{
				break;
			}
			try
			{
				long tickCount = Environment.TickCount64;
				ServeSyncSecureFieldRequests();
				if (_uiaRunning && Interlocked.Exchange(ref _uiaForegroundProbePending, 0) != 0)
				{
					RunForegroundSecureFieldProbe();
				}
				if (_uiaRunning && Environment.TickCount64 >= num2)
				{
					RunTextSelectionPoll();
					num2 = Environment.TickCount64 + num;
				}
				long num3 = Environment.TickCount64 - tickCount;
				if (num3 > 10000 && !_uiaSlowOpLogged)
				{
					_uiaSlowOpLogged = true;
					KuroLog.SystemEvent($"uia worker op took {num3}ms; secure-field/text-selection coverage is stalled downstream of the probe", "UiaWorkerProc", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Input\\InputMonitor.cs", 2939);
				}
			}
			catch (Exception ex)
			{
				NoteCallbackFailure("uia_worker", ex);
			}
		}
		ServeSyncSecureFieldRequests(probe: false);
	}

	private void ServeSyncSecureFieldRequests(bool probe = true)
	{
		SecureFieldRequest[] array;
		lock (_uiaSyncRequests)
		{
			if (_uiaSyncRequests.Count == 0)
			{
				return;
			}
			array = _uiaSyncRequests.ToArray();
			_uiaSyncRequests.Clear();
		}
		bool isSecure = false;
		nint foregroundWindow = GetForegroundWindow();
		string bundleId = Volatile.Read(in _app).BundleId;
		if (probe && _featureFlags.UiaSecureFieldEnabled && !UiaGuard.IsHostileWindow(foregroundWindow, bundleId, _featureFlags))
		{
			GetWindowThreadProcessId(foregroundWindow, out var pid);
			isSecure = UiaProbeHost.Shared.Probe(UiaProbeSite.SecureFieldBridge, UiaProbeReads.SecureField, bundleId, UiaGuard.ClassNameOf(foregroundWindow), (int)pid, ((IntPtr)foregroundWindow).ToInt64(), AppConfig.UiaProbeBridgeDeadlineMs).IsSecure;
		}
		SecureFieldRequest[] array2 = array;
		foreach (SecureFieldRequest obj in array2)
		{
			obj.IsSecure = isSecure;
			obj.Done.Set();
		}
	}

	private void RunForegroundSecureFieldProbe()
	{
		DesktopFeatureFlags featureFlags = _featureFlags;
		if (!featureFlags.UiaSecureFieldEnabled)
		{
			UpdateSecureFieldState(isSecure: false);
			return;
		}
		string bundleId = Volatile.Read(in _app).BundleId;
		if (!BrowserCatalog.IsBrowserExe(bundleId))
		{
			nint foregroundWindow = GetForegroundWindow();
			if (!UiaGuard.IsHostileWindow(foregroundWindow, bundleId, featureFlags))
			{
				GetWindowThreadProcessId(foregroundWindow, out var pid);
				UpdateSecureFieldState(UiaProbeHost.Shared.Probe(UiaProbeSite.ForegroundProbe, UiaProbeReads.SecureField, bundleId, UiaGuard.ClassNameOf(foregroundWindow), (int)pid, ((IntPtr)foregroundWindow).ToInt64(), AppConfig.UiaProbeForegroundDeadlineMs).IsSecure);
			}
		}
	}

	private void RunTextSelectionPoll()
	{
		DesktopFeatureFlags featureFlags = _featureFlags;
		if (!featureFlags.TextSelectionEnabled && !featureFlags.UiaSecureFieldEnabled)
		{
			return;
		}
		AppContext appContext = Volatile.Read(in _app);
		if (BrowserCatalog.IsBrowserExe(appContext.BundleId))
		{
			return;
		}
		nint foregroundWindow = GetForegroundWindow();
		if (!UiaGuard.IsHostileWindow(foregroundWindow, appContext.BundleId, featureFlags))
		{
			GetWindowThreadProcessId(foregroundWindow, out var pid);
			UiaProbeReads uiaProbeReads = UiaProbeReads.None;
			if (featureFlags.UiaSecureFieldEnabled)
			{
				uiaProbeReads |= UiaProbeReads.SecureField;
			}
			if (featureFlags.TextSelectionEnabled)
			{
				uiaProbeReads |= UiaProbeReads.TextSelection;
			}
			UiaProbeResult uiaProbeResult = UiaProbeHost.Shared.Probe(UiaProbeSite.TextSelectionPoll, uiaProbeReads, appContext.BundleId, UiaGuard.ClassNameOf(foregroundWindow), (int)pid, ((IntPtr)foregroundWindow).ToInt64(), AppConfig.UiaProbePollDeadlineMs);
			if (featureFlags.UiaSecureFieldEnabled)
			{
				UpdateSecureFieldState(uiaProbeResult.IsSecure);
			}
			string selectedText = uiaProbeResult.SelectedText;
			if (!string.IsNullOrEmpty(selectedText))
			{
				long num = NowMs();
				Log(new
				{
					type = "text_selection",
					text = selectedText,
					appName = appContext.AppName,
					bundleId = appContext.BundleId,
					selectionLength = selectedText.Length,
					timestamp = num
				}, num);
			}
		}
	}

	private static long NowMs()
	{
		return (long)RecordingClock.Shared.NowMs();
	}

	private void Log(object payload, long timestampMs)
	{
		Volatile.Read(in _eventSink)?.Log(payload, timestampMs);
	}

	private void NoteCallbackFailure(string source, Exception ex)
	{
		if (_callbackFailuresLogged.TryAdd(source, 1))
		{
			KuroLog.SystemEvent($"input callback failure source={source}: {ex.GetType().Name}: {ex.Message}", "NoteCallbackFailure", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Input\\InputMonitor.cs", 3096);
		}
	}

	private (string Name, string BundleId, string? ExePath) GetAppInfo(uint pid, string? windowTitle, nint hwnd, ProcessSighting sighting)
	{
		long num = NowMs();
		ProcessEntry value = null;
		string text;
		if (sighting != ProcessSighting.None && _processes.TryGetValue(pid, out value))
		{
			value.LastSeenMs = num;
			text = value.Path;
		}
		else
		{
			nint num2 = OpenProcess(1052672, inherit: false, pid);
			if (num2 == IntPtr.Zero)
			{
				return UnknownProcess(pid, hwnd);
			}
			StringBuilder stringBuilder = new StringBuilder(1024);
			int sz = 1024;
			if (!QueryFullProcessImageName(num2, 0, stringBuilder, ref sz))
			{
				CloseHandle(num2);
				return UnknownProcess(pid, hwnd);
			}
			text = stringBuilder.ToString();
			if (sighting == ProcessSighting.None)
			{
				CloseHandle(num2);
			}
			else
			{
				if (_processes.Count >= 64)
				{
					KeyValuePair<uint, ProcessEntry> keyValuePair = _processes.MinBy((KeyValuePair<uint, ProcessEntry> p) => (Launched: p.Value.Launched, LastSeenMs: p.Value.LastSeenMs));
					CloseHandle(keyValuePair.Value.Handle);
					_processes.Remove(keyValuePair.Key);
				}
				value = new ProcessEntry
				{
					Handle = num2,
					Path = text,
					Exe = Path.GetFileName(text),
					LastSeenMs = num
				};
				_processes[pid] = value;
			}
			if (!_appNames.ContainsKey(text))
			{
				RequestAppName(text);
			}
		}
		string fileName = Path.GetFileName(text);
		_appNames.TryGetValue(text, out string value2);
		string text2 = ResolveAppName(text, fileName, value2, windowTitle);
		if (sighting == ProcessSighting.Foreground && value != null && !value.Launched)
		{
			value.Launched = true;
			Log(new
			{
				type = "app_launch",
				name = text2,
				bundleId = fileName,
				pid = (int)pid,
				source = "foreground",
				timestamp = num
			}, num);
		}
		return (Name: text2, BundleId: fileName, ExePath: text);
	}

	private (string Name, string BundleId, string? ExePath) UnknownProcess(uint pid, nint hwnd)
	{
		if (Interlocked.Exchange(ref _appInfoResolveDeniedLogged, 1) == 0)
		{
			_unresolvedPid = pid;
			Volatile.Write(ref _unresolvedLineOwed, 1);
			_appNameWake.Set();
		}
		string text = ((hwnd != IntPtr.Zero) ? UiaGuard.ClassNameOf(hwnd) : "");
		return (Name: (text != "") ? text : "unknown", BundleId: "unknown.exe", ExePath: null);
	}

	private void PollExitedProcesses()
	{
		try
		{
			List<uint> list = null;
			foreach (var (item, processEntry2) in _processes)
			{
				if (WaitForSingleObject(processEntry2.Handle, 0u) == 0)
				{
					(list ?? (list = new List<uint>())).Add(item);
				}
			}
			if (list == null)
			{
				return;
			}
			long num2 = NowMs();
			foreach (uint item2 in list)
			{
				ProcessEntry processEntry3 = _processes[item2];
				CloseHandle(processEntry3.Handle);
				_processes.Remove(item2);
				if (processEntry3.Launched)
				{
					_appNames.TryGetValue(processEntry3.Path, out string value);
					string name = ResolveAppName(processEntry3.Path, processEntry3.Exe, value, null);
					Log(new
					{
						type = "app_terminate",
						name = name,
						bundleId = processEntry3.Exe,
						pid = (int)item2,
						source = "foreground",
						timestamp = num2
					}, num2);
				}
			}
		}
		catch (Exception ex)
		{
			NoteCallbackFailure("process_exit_poll", ex);
		}
	}

	private void RequestAppName(string path)
	{
		if (_appNames.TryAdd(path, null))
		{
			_appNameRequests.Enqueue(path);
			_appNameWake.Set();
		}
	}

	private void AppNameWorkerProc()
	{
		while (_appNameRunning)
		{
			_appNameWake.WaitOne();
			WriteHookLines();
			WriteInputSourceChanges();
			string result;
			while (_appNameRunning && _appNameRequests.TryDequeue(out result))
			{
				string value = null;
				try
				{
					FileVersionInfo versionInfo = FileVersionInfo.GetVersionInfo(result);
					value = CleanMetadata(versionInfo.FileDescription) ?? CleanMetadata(versionInfo.ProductName);
				}
				catch
				{
				}
				_appNames[result] = value;
				nint num = Volatile.Read(in _inputWindowHandle);
				if (num != IntPtr.Zero)
				{
					PostMessage(num, 32772u, IntPtr.Zero, IntPtr.Zero);
				}
			}
		}
		WriteHookLines();
		WriteInputSourceChanges();
	}

	private void WriteInputSourceChanges()
	{
		(long, long) result;
		while (_inputSourceRequests.TryDequeue(out result))
		{
			if (!_layoutNames.TryGetValue(result.Item1, out (string, string) value))
			{
				value = DescribeLayout(result.Item1);
				_layoutNames[result.Item1] = value;
			}
			Log(new
			{
				type = "input_source_change",
				hkl = ((uint)result.Item1).ToString("x8"),
				inputSourceName = value.Item1,
				inputSourceId = value.Item2,
				timestamp = result.Item2
			}, result.Item2);
			_frameState?.UpdateInputSource(value.Item1, value.Item2);
		}
	}

	private static (string Name, string Id) DescribeLayout(long hkl)
	{
		int num = (int)(hkl & 0xFFFF);
		int num2 = (int)((hkl >> 16) & 0xFFFF);
		string text = null;
		string item;
		try
		{
			CultureInfo cultureInfo = CultureInfo.GetCultureInfo(num);
			item = cultureInfo.Name;
			text = cultureInfo.EnglishName;
		}
		catch (CultureNotFoundException)
		{
			item = num.ToString("x4");
		}
		try
		{
			using RegistryKey registryKey = Registry.LocalMachine.OpenSubKey("SYSTEM\\CurrentControlSet\\Control\\Keyboard Layouts");
			string text2 = (num2 & 0xF000) switch
			{
				61440 => SubstituteLayoutKlid(registryKey, num2 & 0xFFF), 
				57344 => ((uint)hkl).ToString("x8"), 
				_ => ((num2 != 0) ? num2 : num).ToString("x8"), 
			};
			if (text2 != null)
			{
				using RegistryKey registryKey2 = registryKey?.OpenSubKey(text2);
				if (registryKey2?.GetValue("Layout Text") is string text3 && text3 != "")
				{
					text = text3;
				}
			}
		}
		catch
		{
		}
		return (Name: text ?? "Unknown", Id: item);
	}

	private static string? SubstituteLayoutKlid(RegistryKey? layouts, int layoutId)
	{
		if (layouts == null)
		{
			return null;
		}
		string[] subKeyNames = layouts.GetSubKeyNames();
		foreach (string text in subKeyNames)
		{
			using RegistryKey registryKey = layouts.OpenSubKey(text);
			if (registryKey?.GetValue("Layout Id") is string s && int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var result) && result == layoutId)
			{
				return text;
			}
		}
		return null;
	}

	private void WriteHookLines()
	{
		InstallLine installLine = Interlocked.Exchange(ref _installLine, null);
		if ((object)installLine != null)
		{
			if (installLine.Failed)
			{
				KuroLog.SystemEvent(installLine.Text, "WriteHookLines", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Input\\InputMonitor.cs", 3370);
			}
			else
			{
				KuroLog.LocalOnly("system", installLine.Text, "WriteHookLines", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Input\\InputMonitor.cs", 3371);
			}
		}
		RearmLine result;
		while (_rearmLines.TryDequeue(out result))
		{
			KuroLog.SystemEvent($"input hooks {(result.Installed ? "re-armed" : "re-arm failed")} by liveness watchdog mouse={result.Mouse} keyboard={result.Keyboard} last_err={result.LastError} channel={ChannelNames[result.Channel]} silence_ms={result.SilenceMs} witness={result.Witness}", "WriteHookLines", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Input\\InputMonitor.cs", 3376);
		}
		if (Interlocked.Exchange(ref _unresolvedLineOwed, 0) != 0)
		{
			KuroLog.LocalOnly("system", $"foreground process identity unresolved pid={_unresolvedPid}; events will carry unknown.exe (protected process or pid exit race)", "WriteHookLines", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Input\\InputMonitor.cs", 3381);
		}
	}

	private void OnAppNameResolved()
	{
		try
		{
			string foregroundPath = _foregroundPath;
			if (foregroundPath == null || !_appNames.TryGetValue(foregroundPath, out string value) || value == null)
			{
				return;
			}
			AppContext appContext = Volatile.Read(in _app);
			if (!(value == appContext.AppName))
			{
				Volatile.Write(ref _app, appContext with
				{
					AppName = value
				});
				ActiveAppSnapshot activeAppSnapshot = Volatile.Read(in _activeApp);
				if (activeAppSnapshot != null)
				{
					activeAppSnapshot = activeAppSnapshot with
					{
						Name = value
					};
					Volatile.Write(ref _activeApp, activeAppSnapshot);
					_frameState?.UpdateActiveApp(activeAppSnapshot);
				}
				WindowTitleChanged?.Invoke(new ForegroundWindowInfo(appContext.BundleId, value, activeAppSnapshot?.WindowTitle ?? "", appContext.DisplayIndex, _lastBrowserUrl, appContext.Pid, appContext.Hwnd));
			}
		}
		catch (Exception ex)
		{
			NoteCallbackFailure("app_name", ex);
		}
	}

	private static string ResolveAppName(string path, string exe, string? metadataName, string? windowTitle)
	{
		string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(exe);
		if (metadataName != null)
		{
			return metadataName;
		}
		string text = InferTerminalShellName(windowTitle);
		if (!string.IsNullOrWhiteSpace(text))
		{
			return text;
		}
		string text2 = InferNameFromExecutable(path, fileNameWithoutExtension);
		if (!string.IsNullOrWhiteSpace(text2))
		{
			return text2;
		}
		return fileNameWithoutExtension;
	}

	private static string? CleanMetadata(string? value)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			return null;
		}
		string text = value.Trim();
		if (text.Equals("Microsoft Windows Operating System", StringComparison.OrdinalIgnoreCase) || text.Equals("Microsoft® Windows® Operating System", StringComparison.OrdinalIgnoreCase))
		{
			return null;
		}
		return text;
	}

	private static string? InferTerminalShellName(string? windowTitle)
	{
		if (string.IsNullOrWhiteSpace(windowTitle))
		{
			return null;
		}
		string text = windowTitle.Trim();
		if (text.Contains("PowerShell", StringComparison.OrdinalIgnoreCase))
		{
			if (!text.Contains("Windows PowerShell", StringComparison.OrdinalIgnoreCase))
			{
				return "PowerShell";
			}
			return "Windows PowerShell";
		}
		if (text.Contains("Command Prompt", StringComparison.OrdinalIgnoreCase) || text.Contains("cmd.exe", StringComparison.OrdinalIgnoreCase))
		{
			return "Command Prompt";
		}
		if (text.Contains("Ubuntu", StringComparison.OrdinalIgnoreCase) || text.Contains("WSL", StringComparison.OrdinalIgnoreCase))
		{
			return "WSL";
		}
		return null;
	}

	private static string? InferNameFromExecutable(string path, string exeBase)
	{
		if (exeBase.Equals("powershell", StringComparison.OrdinalIgnoreCase))
		{
			return "Windows PowerShell";
		}
		if (exeBase.Equals("pwsh", StringComparison.OrdinalIgnoreCase))
		{
			return "PowerShell";
		}
		if (exeBase.Equals("windowsterminal", StringComparison.OrdinalIgnoreCase))
		{
			return "Windows Terminal";
		}
		if (exeBase.Equals("cmd", StringComparison.OrdinalIgnoreCase))
		{
			return "Command Prompt";
		}
		if (exeBase.Equals("code", StringComparison.OrdinalIgnoreCase))
		{
			return "Visual Studio Code";
		}
		if (path.Contains("\\WindowsApps\\", StringComparison.OrdinalIgnoreCase))
		{
			return exeBase;
		}
		return null;
	}

	private static bool IsWindowFullscreen(nint hwnd)
	{
		if (!TryGetWindowRect(hwnd, out var rect))
		{
			return false;
		}
		MonitorTable current = MonitorTable.Current;
		if (current.Screens.Count == 0)
		{
			return false;
		}
		Rectangle bounds = current.Screens[current.IndexOfWindow(hwnd)].Bounds;
		if (rect.Left <= bounds.Left && rect.Top <= bounds.Top && rect.Right >= bounds.Right)
		{
			return rect.Bottom >= bounds.Bottom;
		}
		return false;
	}

	private void UpdateSecureFieldState(bool isSecure)
	{
		if (_uiaRunning && _lastSecureFieldFocused != isSecure)
		{
			_lastSecureFieldFocused = isSecure;
			SecureFieldChanged?.Invoke(isSecure);
		}
	}

	public void Dispose()
	{
		Stop();
		_touchpad?.Dispose();
		_hookThreadIdPublished.Dispose();
		_installed.Dispose();
	}
}
