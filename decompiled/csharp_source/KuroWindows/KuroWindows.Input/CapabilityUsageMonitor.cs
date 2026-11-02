using System;
using System.Threading;
using System.Timers;
using KuroWindows.RecorderCore.Clock;
using Microsoft.Win32;

namespace KuroWindows.Input;

internal abstract class CapabilityUsageMonitor : IDisposable
{
	private readonly string _capabilityName;

	private readonly string _eventType;

	private readonly int _pollIntervalMs;

	private IEventSink? _eventSink;

	private System.Timers.Timer? _pollTimer;

	private bool? _lastActive;

	private int _pollInProgress;

	private int _consentReadFailureLogged;

	private bool _consentKeyAbsentLogged;

	public Action<bool>? ActiveChanged { get; set; }

	public bool IsActive => _lastActive == true;

	protected CapabilityUsageMonitor(string capabilityName, string eventType, int pollIntervalMs)
	{
		_capabilityName = capabilityName;
		_eventType = eventType;
		_pollIntervalMs = Math.Max(100, pollIntervalMs);
	}

	public void SetEventSink(IEventSink? sink)
	{
		Volatile.Write(ref _eventSink, sink);
	}

	public void Start()
	{
		_pollTimer = new System.Timers.Timer(_pollIntervalMs);
		_pollTimer.Elapsed += (object? _, ElapsedEventArgs _) =>
		{
			Poll();
		};
		_pollTimer.Start();
		Poll();
	}

	private void Poll()
	{
		if (Interlocked.Exchange(ref _pollInProgress, 1) != 0)
		{
			return;
		}
		try
		{
			bool flag = IsCapabilityActive(_capabilityName);
			if (_lastActive != flag)
			{
				_lastActive = flag;
				try
				{
					ActiveChanged?.Invoke(flag);
				}
				catch (Exception value)
				{
					KuroLog.SystemEvent($"{_eventType} ActiveChanged failed: {value}", "Poll", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Input\\CapabilityUsageMonitor.cs", 67);
				}
				long num = (long)RecordingClock.Shared.NowMs();
				Volatile.Read(in _eventSink)?.Log(new
				{
					type = _eventType,
					isActive = flag,
					timestamp = num
				}, num);
			}
		}
		catch (Exception value2)
		{
			KuroLog.SystemEvent($"{_eventType} poll failed: {value2}", "Poll", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Input\\CapabilityUsageMonitor.cs", 80);
		}
		finally
		{
			Interlocked.Exchange(ref _pollInProgress, 0);
		}
	}

	private bool IsCapabilityActive(string capabilityName)
	{
		try
		{
			using RegistryKey registryKey = Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\CapabilityAccessManager\\ConsentStore\\" + capabilityName);
			if (registryKey == null)
			{
				if (!_consentKeyAbsentLogged)
				{
					_consentKeyAbsentLogged = true;
					KuroLog.LocalOnly("system", _eventType + " consent store key absent; capability detection reports inactive", "IsCapabilityActive", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Input\\CapabilityUsageMonitor.cs", 100);
				}
				return false;
			}
			return HasActiveUsage(registryKey, MinLiveUsageStartFileTime());
		}
		catch (Exception ex)
		{
			if (Interlocked.Exchange(ref _consentReadFailureLogged, 1) == 0)
			{
				KuroLog.SystemEvent($"{_eventType} consent store read failed: {ex.GetType().Name}: {ex.Message}", "IsCapabilityActive", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Input\\CapabilityUsageMonitor.cs", 112);
			}
			return false;
		}
	}

	private static long MinLiveUsageStartFileTime()
	{
		return DateTime.UtcNow.ToFileTimeUtc() - Environment.TickCount64 * 10000 - 150000000;
	}

	private static bool HasActiveUsage(RegistryKey key, long minLiveStartFileTime)
	{
		if (TryReadTimestamp(key, "LastUsedTimeStart", out var value) && TryReadTimestamp(key, "LastUsedTimeStop", out var value2) && value >= minLiveStartFileTime && (value2 == 0L || value > value2))
		{
			return true;
		}
		string[] subKeyNames = key.GetSubKeyNames();
		foreach (string name in subKeyNames)
		{
			using RegistryKey registryKey = key.OpenSubKey(name);
			if (registryKey != null && HasActiveUsage(registryKey, minLiveStartFileTime))
			{
				return true;
			}
		}
		return false;
	}

	private static bool TryReadTimestamp(RegistryKey key, string valueName, out long value)
	{
		value = 0L;
		object value2 = key.GetValue(valueName);
		if (!(value2 is long num))
		{
			if (!(value2 is int num2))
			{
				if (value2 is byte[] array && array.Length >= 8)
				{
					value = BitConverter.ToInt64(array, 0);
					return true;
				}
				return false;
			}
			value = num2;
			return true;
		}
		value = num;
		return true;
	}

	public void Dispose()
	{
		_pollTimer?.Stop();
		_pollTimer?.Dispose();
		_pollTimer = null;
	}
}
