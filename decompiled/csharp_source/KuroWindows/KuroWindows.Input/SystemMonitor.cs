using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Timers;
using System.Windows.Forms;
using KuroWindows.RecorderCore.Clock;
using Microsoft.Win32;
using Windows.Networking.Connectivity;

namespace KuroWindows.Input;

public sealed class SystemMonitor : IDisposable
{
	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
	private struct MEMORYSTATUSEX
	{
		public uint dwLength;

		public uint dwMemoryLoad;

		public ulong ullTotalPhys;

		public ulong ullAvailPhys;

		public ulong ullTotalPageFile;

		public ulong ullAvailPageFile;

		public ulong ullTotalVirtual;

		public ulong ullAvailVirtual;

		public ulong ullAvailExtendedVirtual;
	}

	private IEventSink? _eventSink;

	private FrameStateSource? _frameState;

	private string _lastSsid = "";

	private bool _lastCharging;

	private int _lastBatteryPct = -1;

	private string _lastAppearance = "";

	private bool? _lastNetworkConnected;

	private string? _lastInterfaceType;

	private bool _lastIsExpensive;

	private bool _lastIsConstrained;

	private bool? _lastIsOnAc;

	private string _lastMemoryPressure = "";

	private System.Timers.Timer? _pollTimer;

	private bool _pollFailureLogged;

	private static int _ssidProbeLogged;

	public Action<string>? InputSettingsChanged;

	private int _pollInProgress;

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);

	public void SetFrameStateSource(FrameStateSource? source)
	{
		_frameState = source;
	}

	public void SetEventSink(IEventSink? sink)
	{
		Volatile.Write(ref _eventSink, sink);
	}

	public void Start()
	{
		SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
		SystemEvents.PowerModeChanged += OnPowerModeChanged;
		SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
		NetworkInformation.NetworkStatusChanged += OnNetworkChanged;
		_pollTimer = new System.Timers.Timer(5000.0);
		_pollTimer.Elapsed += (object? _, ElapsedEventArgs _) =>
		{
			PollChanges();
		};
		_pollTimer.Start();
		LogInitialState();
	}

	public void LogInitialState()
	{
		LogAppearance(force: true);
		LogBattery(force: true);
		LogWifi(force: true);
		LogNetworkChange(force: true);
		LogPowerSourceChange(force: true);
		LogMemoryPressure(force: true);
	}

	private void OnDisplayChanged(object? sender, EventArgs e)
	{
		MonitorTable.Invalidate();
		long num = (long)RecordingClock.Shared.NowMs();
		Log(ScreenConfigRow("display_change", null, num), num);
	}

	private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
	{
		bool flag;
		switch (e.Category)
		{
		case UserPreferenceCategory.General:
		case UserPreferenceCategory.Keyboard:
		case UserPreferenceCategory.Mouse:
			flag = true;
			break;
		default:
			flag = false;
			break;
		}
		if (flag)
		{
			InputSettingsChanged?.Invoke("user_preference");
		}
	}

	private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
	{
		if (e.Mode == PowerModes.StatusChange)
		{
			LogBattery(force: true);
			LogPowerSourceChange(force: true);
		}
	}

	private void OnNetworkChanged(object? sender)
	{
		LogNetworkChange(force: true);
	}

	private void PollChanges()
	{
		if (Interlocked.Exchange(ref _pollInProgress, 1) != 0)
		{
			return;
		}
		try
		{
			LogAppearance(force: false);
			LogBattery(force: false);
			LogWifi(force: false);
			LogNetworkChange(force: false);
			LogPowerSourceChange(force: false);
			LogMemoryPressure(force: false);
		}
		catch (Exception ex)
		{
			if (!_pollFailureLogged)
			{
				_pollFailureLogged = true;
				KuroLog.SystemEvent("system state poll failed: " + ex.GetType().Name + ": " + ex.Message, "PollChanges", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Input\\SystemMonitor.cs", 141);
			}
		}
		finally
		{
			Interlocked.Exchange(ref _pollInProgress, 0);
		}
	}

	internal static object ScreenConfigRow(string reason, IReadOnlyDictionary<int, int>? screenToClip, long timestamp)
	{
		var array = MonitorTable.Current.Screens.Select((MonitorTable.Entry s) => new
		{
			index = s.Index,
			originX = s.Bounds.Left,
			originY = s.Bounds.Top,
			width = s.Bounds.Width,
			height = s.Bounds.Height,
			visibleWidth = s.WorkingArea.Width,
			visibleHeight = s.WorkingArea.Height,
			scaleFactor = s.ScaleFactor,
			clipIndex = ((screenToClip != null && screenToClip.TryGetValue(s.Index, out var value)) ? new int?(value) : ((int?)null)),
			deviceName = s.DeviceName,
			primary = s.Primary
		}).ToArray();
		return new
		{
			type = "screen_config",
			screenCount = array.Length,
			screens = array,
			reason = reason,
			timestamp = timestamp
		};
	}

	private void LogAppearance(bool force)
	{
		string text = (ReadDarkMode() ? "dark" : "light");
		if (force || !(text == _lastAppearance))
		{
			_lastAppearance = text;
			long num = (long)RecordingClock.Shared.NowMs();
			Log(new
			{
				type = "appearance_change",
				appearance = text,
				timestamp = num
			}, num);
			_frameState?.UpdateAppearance(text);
		}
	}

	private void LogBattery(bool force)
	{
		PowerStatus powerStatus = SystemInformation.PowerStatus;
		bool flag = powerStatus.PowerLineStatus == PowerLineStatus.Online;
		int num = (int)(powerStatus.BatteryLifePercent * 100f);
		if (num > 100)
		{
			num = -1;
		}
		if (force || flag != _lastCharging || num != _lastBatteryPct)
		{
			_lastCharging = flag;
			_lastBatteryPct = num;
			long num2 = (long)RecordingClock.Shared.NowMs();
			Log(new
			{
				type = "battery_status",
				isCharging = flag,
				batteryLevel = num,
				timestamp = num2
			}, num2);
			_frameState?.UpdateBattery((num >= 0) ? new int?(num) : ((int?)null), flag);
		}
	}

	private void LogWifi(bool force)
	{
		string currentSsid = GetCurrentSsid();
		if (force || !(currentSsid == _lastSsid))
		{
			_lastSsid = currentSsid;
			long num = (long)RecordingClock.Shared.NowMs();
			Log(new
			{
				type = "wifi_change",
				ssid = currentSsid,
				timestamp = num
			}, num);
		}
	}

	private void LogNetworkChange(bool force)
	{
		var (flag, text, flag2, flag3) = GetNetworkState();
		if (force || flag != _lastNetworkConnected || !string.Equals(text, _lastInterfaceType, StringComparison.OrdinalIgnoreCase) || flag2 != _lastIsExpensive || flag3 != _lastIsConstrained)
		{
			_lastNetworkConnected = flag;
			_lastInterfaceType = text;
			_lastIsExpensive = flag2;
			_lastIsConstrained = flag3;
			long num = (long)RecordingClock.Shared.NowMs();
			Log(new
			{
				type = "network_change",
				isConnected = flag,
				interfaceType = text,
				isExpensive = flag2,
				isConstrained = flag3,
				timestamp = num
			}, num);
			_frameState?.UpdateNetwork(flag, text);
		}
	}

	private void LogPowerSourceChange(bool force)
	{
		PowerStatus powerStatus = SystemInformation.PowerStatus;
		bool flag = powerStatus.PowerLineStatus == PowerLineStatus.Online;
		int num = (int)(powerStatus.BatteryLifePercent * 100f);
		if (num > 100)
		{
			num = -1;
		}
		if (force || _lastIsOnAc != flag)
		{
			_lastIsOnAc = flag;
			long num2 = (long)RecordingClock.Shared.NowMs();
			Log(new
			{
				type = "power_source_change",
				isOnAC = flag,
				batteryLevel = num,
				timestamp = num2
			}, num2);
			_frameState?.UpdatePowerSource(flag);
		}
	}

	private void LogMemoryPressure(bool force)
	{
		string memoryPressureLevel = GetMemoryPressureLevel();
		if (force || !(memoryPressureLevel == _lastMemoryPressure))
		{
			_lastMemoryPressure = memoryPressureLevel;
			long num = (long)RecordingClock.Shared.NowMs();
			Log(new
			{
				type = "memory_pressure",
				pressureLevel = memoryPressureLevel,
				timestamp = num
			}, num);
			_frameState?.UpdateMemoryPressure(memoryPressureLevel);
		}
	}

	private static bool ReadDarkMode()
	{
		try
		{
			using RegistryKey registryKey = Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Themes\\Personalize");
			return registryKey?.GetValue("AppsUseLightTheme") is int num && num == 0;
		}
		catch
		{
			return false;
		}
	}

	private static string GetCurrentSsid()
	{
		try
		{
			foreach (ConnectionProfile connectionProfile in NetworkInformation.GetConnectionProfiles())
			{
				if (connectionProfile.IsWlanConnectionProfile && connectionProfile.GetNetworkConnectivityLevel() != NetworkConnectivityLevel.None)
				{
					return connectionProfile.WlanConnectionProfileDetails.GetConnectedSsid();
				}
			}
		}
		catch (Exception ex)
		{
			if (Interlocked.Exchange(ref _ssidProbeLogged, 1) == 0)
			{
				KuroLog.LocalOnly("system", "wifi ssid probe unavailable: " + ex.GetType().Name, "GetCurrentSsid", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Input\\SystemMonitor.cs", 320);
			}
		}
		return "";
	}

	private static (bool connected, string? interfaceType, bool isExpensive, bool isConstrained) GetNetworkState()
	{
		try
		{
			ConnectionProfile internetConnectionProfile = NetworkInformation.GetInternetConnectionProfile();
			if (internetConnectionProfile == null)
			{
				return (connected: false, interfaceType: null, isExpensive: false, isConstrained: false);
			}
			bool item = internetConnectionProfile.GetNetworkConnectivityLevel() != NetworkConnectivityLevel.None;
			uint? num = internetConnectionProfile.NetworkAdapter?.IanaInterfaceType;
			string text;
			switch (num)
			{
			case 6u:
				text = "ethernet";
				break;
			case 71u:
				text = "wifi";
				break;
			case 243u:
			case 244u:
				text = "cellular";
				break;
			default:
				text = num?.ToString();
				break;
			}
			string item2 = text;
			ConnectionCost connectionCost = internetConnectionProfile.GetConnectionCost();
			NetworkCostType networkCostType = connectionCost.NetworkCostType;
			bool flag = (uint)(networkCostType - 2) <= 1u;
			bool item3 = flag;
			bool item4 = connectionCost.ApproachingDataLimit || connectionCost.OverDataLimit || connectionCost.Roaming;
			return (connected: item, interfaceType: item2, isExpensive: item3, isConstrained: item4);
		}
		catch
		{
			return (connected: false, interfaceType: null, isExpensive: false, isConstrained: false);
		}
	}

	private static string GetMemoryPressureLevel()
	{
		MEMORYSTATUSEX buffer = new MEMORYSTATUSEX
		{
			dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>()
		};
		if (!GlobalMemoryStatusEx(ref buffer))
		{
			return "unknown";
		}
		switch (buffer.dwMemoryLoad)
		{
		default:
			return "critical";
		case 65u:
		case 66u:
		case 67u:
		case 68u:
		case 69u:
		case 70u:
		case 71u:
		case 72u:
		case 73u:
		case 74u:
		case 75u:
		case 76u:
		case 77u:
		case 78u:
		case 79u:
			return "warning";
		case 0u:
		case 1u:
		case 2u:
		case 3u:
		case 4u:
		case 5u:
		case 6u:
		case 7u:
		case 8u:
		case 9u:
		case 10u:
		case 11u:
		case 12u:
		case 13u:
		case 14u:
		case 15u:
		case 16u:
		case 17u:
		case 18u:
		case 19u:
		case 20u:
		case 21u:
		case 22u:
		case 23u:
		case 24u:
		case 25u:
		case 26u:
		case 27u:
		case 28u:
		case 29u:
		case 30u:
		case 31u:
		case 32u:
		case 33u:
		case 34u:
		case 35u:
		case 36u:
		case 37u:
		case 38u:
		case 39u:
		case 40u:
		case 41u:
		case 42u:
		case 43u:
		case 44u:
		case 45u:
		case 46u:
		case 47u:
		case 48u:
		case 49u:
		case 50u:
		case 51u:
		case 52u:
		case 53u:
		case 54u:
		case 55u:
		case 56u:
		case 57u:
		case 58u:
		case 59u:
		case 60u:
		case 61u:
		case 62u:
		case 63u:
		case 64u:
			return "normal";
		}
	}

	private void Log(object payload, long timestampMs)
	{
		Volatile.Read(in _eventSink)?.Log(payload, timestampMs);
	}

	public void Dispose()
	{
		SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
		SystemEvents.PowerModeChanged -= OnPowerModeChanged;
		SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
		NetworkInformation.NetworkStatusChanged -= OnNetworkChanged;
		_pollTimer?.Stop();
		_pollTimer?.Dispose();
	}
}
