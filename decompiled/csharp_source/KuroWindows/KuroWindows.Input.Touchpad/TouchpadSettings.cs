using System;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Threading;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace KuroWindows.Input.Touchpad;

internal static class TouchpadSettings
{
	private struct TOUCHPAD_PARAMETERS
	{
		public uint versionNumber;

		public uint maxSupportedContacts;

		public uint legacyTouchpadFeatures;

		public uint presence;

		public uint behaviour;

		public uint sensitivityLevel;

		public uint cursorSpeed;

		public uint feedbackIntensity;

		public uint clickForceSensitivity;

		public uint rightClickZoneWidth;

		public uint rightClickZoneHeight;
	}

	public sealed class RegistryWatch : IDisposable
	{
		private readonly object _sync = new object();

		private readonly RegistryKey? _key;

		private readonly ManualResetEvent _signal = new ManualResetEvent(initialState: false);

		private readonly Action _changed;

		private readonly Action<Exception> _failed;

		private RegisteredWaitHandle? _wait;

		private bool _disposed;

		public RegistryWatch(Action changed, Action<Exception> failed)
		{
			_changed = changed;
			_failed = failed;
			_key = Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\PrecisionTouchPad", RegistryKeyPermissionCheck.ReadSubTree, RegistryRights.ExecuteKey);
			Arm();
		}

		private bool Arm()
		{
			lock (_sync)
			{
				if (_key == null || _disposed)
				{
					return false;
				}
				_signal.Reset();
				if (RegNotifyChangeKeyValue(_key.Handle, watchSubtree: false, 268435460u, _signal.SafeWaitHandle, asynchronous: true) == 0)
				{
					_wait = ThreadPool.RegisterWaitForSingleObject(_signal, OnSignal, null, -1, executeOnlyOnce: true);
				}
				return true;
			}
		}

		private void OnSignal(object? state, bool timedOut)
		{
			try
			{
				if (Arm())
				{
					_changed();
				}
			}
			catch (Exception obj)
			{
				_failed(obj);
			}
		}

		public void Dispose()
		{
			lock (_sync)
			{
				if (!_disposed)
				{
					_disposed = true;
					_wait?.Unregister(null);
					_key?.Dispose();
					_signal.Dispose();
				}
			}
		}
	}

	private const uint SPI_GETTOUCHPADPARAMETERS = 174u;

	private const uint TOUCHPAD_PARAMETERS_LATEST_VERSION = 1u;

	private const string KeyPath = "Software\\Microsoft\\Windows\\CurrentVersion\\PrecisionTouchPad";

	private const uint REG_NOTIFY_CHANGE_LAST_SET = 4u;

	private const uint REG_NOTIFY_THREAD_AGNOSTIC = 268435456u;

	[DllImport("user32.dll", SetLastError = true)]
	private static extern bool SystemParametersInfoW(uint action, uint param, ref TOUCHPAD_PARAMETERS parameters, uint winIni);

	[DllImport("advapi32.dll")]
	private static extern int RegNotifyChangeKeyValue(SafeRegistryHandle key, bool watchSubtree, uint filter, SafeWaitHandle hEvent, bool asynchronous);

	public static TouchpadSettingsSnapshot Read()
	{
		TOUCHPAD_PARAMETERS parameters = new TOUCHPAD_PARAMETERS
		{
			versionNumber = 1u
		};
		if (SystemParametersInfoW(174u, (uint)Marshal.SizeOf<TOUCHPAD_PARAMETERS>(), ref parameters, 0u))
		{
			return new TouchpadSettingsSnapshot("spi", (parameters.presence & 1) != 0, (parameters.presence & 2) != 0, (parameters.presence & 4) != 0, (parameters.presence & 8) != 0, (parameters.presence & 0x10) != 0, (parameters.behaviour & 0x80) != 0, (parameters.behaviour & 0x100) != 0, (parameters.behaviour & 0x200) != 0, (int)parameters.maxSupportedContacts, (int)parameters.sensitivityLevel, (int)parameters.cursorSpeed, (parameters.behaviour & 4) != 0, (parameters.behaviour & 0x10) != 0, (parameters.behaviour & 1) != 0);
		}
		using RegistryKey registryKey = Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\PrecisionTouchPad");
		if (registryKey == null)
		{
			return new TouchpadSettingsSnapshot("default", null, null, null, null, null, Pan: true, Zoom: true, Reversed: false, null, null, null, Tap: true, TwoFingerTap: true, LeaveOnWithMouse: true);
		}
		return new TouchpadSettingsSnapshot("registry", null, null, null, null, null, Flag(registryKey, "PanEnabled", absent: true), Flag(registryKey, "ZoomEnabled", absent: true), Flag(registryKey, "ScrollDirection", absent: false), null, Number(registryKey, "AAPThreshold"), Number(registryKey, "CursorSpeed"), Flag(registryKey, "TapsEnabled", absent: true), Flag(registryKey, "TwoFingerTapEnabled", absent: true), Flag(registryKey, "LeaveOnWithMouse", absent: true));
	}

	private static bool Flag(RegistryKey key, string name, bool absent)
	{
		if (!(key.GetValue(name) is int num))
		{
			return absent;
		}
		return num != 0;
	}

	private static int? Number(RegistryKey key, string name)
	{
		object value = key.GetValue(name);
		if (!(value is int))
		{
			return null;
		}
		return (int)value;
	}
}
