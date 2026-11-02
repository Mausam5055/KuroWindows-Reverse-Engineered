using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using DeviceIdentity;
using KuroWindows.Input.Touchpad;

namespace KuroWindows.Input;

internal sealed class InputFactsAccumulator
{
	public const int MaxExtraInfoTags = 4;

	public const int MaxDevices = 6;

	private const int MaxTrackedTags = 16;

	private const int MaxCachedHandles = 32;

	private const int MaxDeviceNameChars = 48;

	private long _events;

	private long _injected;

	private long _injectedMouse;

	private long _injectedKey;

	private long _rawPackets;

	private long _rawNullDevice;

	private long _rawAbsolute;

	private long _rawKeyboard;

	private long _rawFailures;

	private long _rearms;

	private long _gapMs;

	private readonly object _sync = new object();

	private readonly Dictionary<ulong, long> _extraInfo = new Dictionary<ulong, long>();

	private readonly Dictionary<nint, InputDeviceFact?> _deviceCache = new Dictionary<nint, InputDeviceFact>();

	private List<nint> _segmentDevices = new List<nint>(6);

	private nint _lastSegmentHandle;

	public void NoteMouse(bool injected, nint extraInfo)
	{
		Interlocked.Increment(ref _events);
		if (injected)
		{
			Interlocked.Increment(ref _injected);
			Interlocked.Increment(ref _injectedMouse);
			NoteExtraInfo(extraInfo);
		}
	}

	public void NoteKey(bool injected, nint extraInfo)
	{
		Interlocked.Increment(ref _events);
		if (injected)
		{
			Interlocked.Increment(ref _injected);
			Interlocked.Increment(ref _injectedKey);
			NoteExtraInfo(extraInfo);
		}
	}

	public void NoteRawFailure()
	{
		Interlocked.Increment(ref _rawFailures);
	}

	public void NoteRawKeyboard()
	{
		Interlocked.Increment(ref _rawKeyboard);
	}

	public void NoteRearm()
	{
		Interlocked.Increment(ref _rearms);
	}

	public void NoteGap(long gapMs)
	{
		Interlocked.Add(ref _gapMs, gapMs);
	}

	public void ForgetDevice(nint hDevice)
	{
		lock (_sync)
		{
			_deviceCache.Remove(hDevice);
			if (hDevice == Volatile.Read(in _lastSegmentHandle))
			{
				Volatile.Write(ref _lastSegmentHandle, IntPtr.Zero);
			}
		}
	}

	public void NoteRawPacket(nint hDevice, bool absolute)
	{
		Interlocked.Increment(ref _rawPackets);
		if (absolute)
		{
			Interlocked.Increment(ref _rawAbsolute);
		}
		if (hDevice == IntPtr.Zero)
		{
			Interlocked.Increment(ref _rawNullDevice);
		}
		else
		{
			if (hDevice == Volatile.Read(in _lastSegmentHandle))
			{
				return;
			}
			lock (_sync)
			{
				if (!_segmentDevices.Contains(hDevice) && _segmentDevices.Count < 6)
				{
					if (!_deviceCache.ContainsKey(hDevice))
					{
						if (_deviceCache.Count >= 32)
						{
							_deviceCache.Clear();
						}
						_deviceCache[hDevice] = Describe(hDevice);
					}
					_segmentDevices.Add(hDevice);
				}
				Volatile.Write(ref _lastSegmentHandle, hDevice);
			}
		}
	}

	public InputFactsSnapshot SnapshotAndReset()
	{
		List<InputExtraInfoFact> extraInfo;
		List<InputDeviceFact> list;
		lock (_sync)
		{
			extraInfo = (from pair in (from pair in _extraInfo
					orderby pair.Value descending, pair.Key
					select pair).Take(4)
				select new InputExtraInfoFact(pair.Key.ToString("x"), pair.Value)).ToList();
			_extraInfo.Clear();
			list = new List<InputDeviceFact>(_segmentDevices.Count);
			foreach (nint segmentDevice in _segmentDevices)
			{
				if (_deviceCache.TryGetValue(segmentDevice, out InputDeviceFact value) && value != null)
				{
					list.Add(value);
				}
			}
			_segmentDevices = new List<nint>(6);
			Volatile.Write(ref _lastSegmentHandle, IntPtr.Zero);
		}
		return new InputFactsSnapshot(Interlocked.Exchange(ref _events, 0L), Interlocked.Exchange(ref _injected, 0L), Interlocked.Exchange(ref _injectedMouse, 0L), Interlocked.Exchange(ref _injectedKey, 0L), extraInfo, Interlocked.Exchange(ref _rawPackets, 0L), Interlocked.Exchange(ref _rawNullDevice, 0L), Interlocked.Exchange(ref _rawAbsolute, 0L), list, Interlocked.Exchange(ref _rawKeyboard, 0L), Interlocked.Exchange(ref _rawFailures, 0L), Interlocked.Exchange(ref _rearms, 0L), Interlocked.Exchange(ref _gapMs, 0L), 0L, null);
	}

	private void NoteExtraInfo(nint extraInfo)
	{
		ulong num = (ulong)((IntPtr)extraInfo).ToInt64();
		if (num == 0L)
		{
			return;
		}
		lock (_sync)
		{
			if (_extraInfo.TryGetValue(num, out var value))
			{
				_extraInfo[num] = value + 1;
			}
			else if (_extraInfo.Count < 16)
			{
				_extraInfo[num] = 1L;
			}
		}
	}

	private static InputDeviceFact? Describe(nint hDevice)
	{
		try
		{
			string kind = "hid";
			string text = null;
			string text2 = null;
			if (HidInterop.TryDeviceInfo(hDevice, out var info))
			{
				kind = info.Type switch
				{
					0u => "mouse", 
					1u => "keyboard", 
					_ => "hid", 
				};
				if (info.Type == 2)
				{
					text = Hex4(info.VendorId);
					text2 = Hex4(info.ProductId);
				}
			}
			string path = HidInterop.DeviceName(hDevice);
			if (text == null)
			{
				text = HexToken(path, "VID_");
			}
			if (text2 == null)
			{
				text2 = HexToken(path, "PID_");
			}
			return new InputDeviceFact(text, text2, kind, TrimDeviceName(path));
		}
		catch
		{
			return null;
		}
	}

	internal static string? TrimDeviceName(string? path)
	{
		if (string.IsNullOrWhiteSpace(path))
		{
			return null;
		}
		string text = path;
		if (text.StartsWith("\\\\?\\", StringComparison.Ordinal) || text.StartsWith("\\??\\", StringComparison.Ordinal))
		{
			text = text.Substring(4);
		}
		int num = text.LastIndexOf("#{", StringComparison.Ordinal);
		if (num > 0)
		{
			text = text.Substring(0, num);
		}
		return HardwareFacets.Text(text, 48);
	}

	internal static string? HexToken(string? path, string token)
	{
		if (string.IsNullOrEmpty(path))
		{
			return null;
		}
		int num = path.IndexOf(token, StringComparison.OrdinalIgnoreCase);
		if (num < 0 || num + token.Length + 4 > path.Length)
		{
			return null;
		}
		string text = path.Substring(num + token.Length, 4);
		if (!text.All(Uri.IsHexDigit))
		{
			return null;
		}
		return text.ToUpperInvariant();
	}

	private static string Hex4(int value)
	{
		return (value & 0xFFFF).ToString("X4");
	}
}
