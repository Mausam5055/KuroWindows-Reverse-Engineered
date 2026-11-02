using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace KuroWindows.Input;

public sealed class FrameStateSource
{
	private readonly object _lock = new object();

	private bool _leftDown;

	private bool _rightDown;

	private bool _middleDown;

	private bool _other3Down;

	private bool _other4Down;

	private readonly HashSet<uint> _pressedKeys = new HashSet<uint>();

	private uint _modifiers;

	private ActiveAppSnapshot? _activeApp;

	private MousePositionEntry? _lastMousePosition;

	private int? _keyboardDisplay;

	private string? _appearance;

	private int? _batteryLevel;

	private bool? _isCharging;

	private bool? _isOnAc;

	private bool? _isOnline;

	private string? _networkInterfaceType;

	private string? _memoryPressure;

	private string? _inputSourceName;

	private string? _inputSourceId;

	private bool _microphoneActive;

	private bool _cameraActive;

	private const long RetentionMs = 5000L;

	private readonly List<ScrollEntry> _scroll = new List<ScrollEntry>();

	private readonly List<ClickEntry> _clicks = new List<ClickEntry>();

	private readonly List<KeyTransitionEntry> _keyTransitions = new List<KeyTransitionEntry>();

	private readonly List<MousePositionEntry> _mousePositions = new List<MousePositionEntry>();

	public void UpdateMousePosition(int x, int y, int screen, long timestampMs)
	{
		lock (_lock)
		{
			AppendLocked(entry: _lastMousePosition = new MousePositionEntry(x, y, screen, timestampMs), list: _mousePositions);
		}
	}

	public MousePositionEntry? LastMousePosition()
	{
		lock (_lock)
		{
			return _lastMousePosition;
		}
	}

	public MousePositionEntry? MousePositionForFrame(long wallMs)
	{
		lock (_lock)
		{
			PruneLocked(_mousePositions, wallMs - 5000);
			for (int num = _mousePositions.Count - 1; num >= 0; num--)
			{
				if (_mousePositions[num].TimestampMs <= wallMs)
				{
					return _mousePositions[num];
				}
			}
			return _lastMousePosition;
		}
	}

	public bool HasRecentActivityForDisplay(int screen, long fromWallMsExclusive, long toWallMsInclusive)
	{
		lock (_lock)
		{
			PruneLocked(_scroll, toWallMsInclusive - 5000);
			PruneLocked(_clicks, toWallMsInclusive - 5000);
			PruneLocked(_keyTransitions, toWallMsInclusive - 5000);
			PruneLocked(_mousePositions, toWallMsInclusive - 5000);
			ReadOnlySpan<KeyTransitionEntry> readOnlySpan = WindowLocked(_keyTransitions, fromWallMsExclusive, toWallMsInclusive);
			long? num = null;
			ReadOnlySpan<KeyTransitionEntry> readOnlySpan2 = readOnlySpan;
			for (int i = 0; i < readOnlySpan2.Length; i++)
			{
				KeyTransitionEntry keyTransitionEntry = readOnlySpan2[i];
				if (keyTransitionEntry.Screen.HasValue && keyTransitionEntry.Screen.Value != screen && (!num.HasValue || keyTransitionEntry.TimestampMs > num.Value))
				{
					num = keyTransitionEntry.TimestampMs;
				}
			}
			ReadOnlySpan<MousePositionEntry> readOnlySpan3 = WindowLocked(_mousePositions, fromWallMsExclusive, toWallMsInclusive);
			for (int i = 0; i < readOnlySpan3.Length; i++)
			{
				MousePositionEntry mousePositionEntry = readOnlySpan3[i];
				if (mousePositionEntry.Screen == screen && (!num.HasValue || mousePositionEntry.TimestampMs > num.Value))
				{
					return true;
				}
			}
			ReadOnlySpan<ScrollEntry> readOnlySpan4 = WindowLocked(_scroll, fromWallMsExclusive, toWallMsInclusive);
			for (int i = 0; i < readOnlySpan4.Length; i++)
			{
				if (readOnlySpan4[i].Screen == screen)
				{
					return true;
				}
			}
			ReadOnlySpan<ClickEntry> readOnlySpan5 = WindowLocked(_clicks, fromWallMsExclusive, toWallMsInclusive);
			for (int i = 0; i < readOnlySpan5.Length; i++)
			{
				if (readOnlySpan5[i].Screen == screen)
				{
					return true;
				}
			}
			ReadOnlySpan<KeyTransitionEntry> readOnlySpan6 = readOnlySpan;
			for (int i = 0; i < readOnlySpan6.Length; i++)
			{
				KeyTransitionEntry entry = readOnlySpan6[i];
				if (KeyBelongsToDisplay(entry, screen))
				{
					return true;
				}
			}
			return _pressedKeys.Count > 0 && KeyboardDisplayLocked() == screen;
		}
	}

	public void UpdateMouseButton(string button, bool isDown)
	{
		lock (_lock)
		{
			switch (button)
			{
			case "left":
				_leftDown = isDown;
				break;
			case "right":
				_rightDown = isDown;
				break;
			case "middle":
				_middleDown = isDown;
				break;
			case "other_3":
				_other3Down = isDown;
				break;
			case "other_4":
				_other4Down = isDown;
				break;
			}
		}
	}

	public void UpdatePressedKey(uint vkCode, bool isDown)
	{
		lock (_lock)
		{
			if (isDown)
			{
				_pressedKeys.Add(vkCode);
			}
			else
			{
				_pressedKeys.Remove(vkCode);
			}
		}
	}

	public void UpdateModifiers(uint mods)
	{
		lock (_lock)
		{
			_modifiers = mods;
		}
	}

	public void ClearKeyboardState()
	{
		lock (_lock)
		{
			_pressedKeys.Clear();
			_modifiers = 0u;
			_keyboardDisplay = null;
		}
	}

	public void SeedKeyboardState(IReadOnlyCollection<uint> vks, uint modifiers)
	{
		lock (_lock)
		{
			_pressedKeys.Clear();
			foreach (uint vk in vks)
			{
				_pressedKeys.Add(vk);
			}
			_modifiers = modifiers;
		}
	}

	public void UpdateActiveApp(ActiveAppSnapshot? snapshot)
	{
		lock (_lock)
		{
			_activeApp = snapshot;
		}
	}

	public void UpdateAppearance(string value)
	{
		lock (_lock)
		{
			_appearance = value;
		}
	}

	public void UpdateBattery(int? level, bool? charging)
	{
		lock (_lock)
		{
			_batteryLevel = level;
			_isCharging = charging;
		}
	}

	public void UpdatePowerSource(bool onAc)
	{
		lock (_lock)
		{
			_isOnAc = onAc;
		}
	}

	public void UpdateNetwork(bool online, string? ifType)
	{
		lock (_lock)
		{
			_isOnline = online;
			_networkInterfaceType = ifType;
		}
	}

	public void UpdateMemoryPressure(string level)
	{
		lock (_lock)
		{
			_memoryPressure = level;
		}
	}

	public void UpdateInputSource(string name, string id)
	{
		lock (_lock)
		{
			_inputSourceName = name;
			_inputSourceId = id;
		}
	}

	public void UpdateMicrophoneActive(bool active)
	{
		lock (_lock)
		{
			_microphoneActive = active;
		}
	}

	public void UpdateCameraActive(bool active)
	{
		lock (_lock)
		{
			_cameraActive = active;
		}
	}

	public void AppendScroll(double dx, double dy, int screen, long timestampMs, string? src = null)
	{
		lock (_lock)
		{
			AppendLocked(_scroll, new ScrollEntry(dx, dy, screen, timestampMs, src));
		}
	}

	public void AppendClick(string button, bool isDown, int x, int y, int screen, long timestampMs)
	{
		lock (_lock)
		{
			AppendLocked(_clicks, new ClickEntry(button, isDown, x, y, screen, timestampMs));
		}
	}

	public void AppendKeyTransition(uint vkCode, string? chars, bool isDown, long timestampMs, int? screen = null)
	{
		lock (_lock)
		{
			if (screen.HasValue)
			{
				_keyboardDisplay = screen.Value;
			}
			AppendLocked(_keyTransitions, new KeyTransitionEntry(vkCode, chars, isDown, timestampMs, screen));
		}
	}

	private int? KeyboardDisplayLocked()
	{
		return _keyboardDisplay ?? _lastMousePosition?.Screen;
	}

	private bool KeyBelongsToDisplay(KeyTransitionEntry entry, int screen)
	{
		if (entry.Screen.HasValue)
		{
			return entry.Screen.Value == screen;
		}
		return KeyboardDisplayLocked() == screen;
	}

	public FrameStateSnapshot SnapshotForFrame(long fromWallMsExclusive, long toWallMsInclusive)
	{
		lock (_lock)
		{
			long cutoffMs = Math.Min(toWallMsInclusive - 5000, fromWallMsExclusive + 1);
			PruneLocked(_scroll, cutoffMs);
			PruneLocked(_clicks, cutoffMs);
			PruneLocked(_keyTransitions, cutoffMs);
			PruneLocked(_mousePositions, cutoffMs);
			return new FrameStateSnapshot
			{
				LeftDown = _leftDown,
				RightDown = _rightDown,
				MiddleDown = _middleDown,
				Other3Down = _other3Down,
				Other4Down = _other4Down,
				KeysDown = _pressedKeys.ToArray(),
				Modifiers = _modifiers,
				ActiveApp = _activeApp,
				Appearance = _appearance,
				BatteryLevel = _batteryLevel,
				IsCharging = _isCharging,
				IsOnAc = _isOnAc,
				IsOnline = _isOnline,
				NetworkInterfaceType = _networkInterfaceType,
				MemoryPressure = _memoryPressure,
				InputSourceName = _inputSourceName,
				InputSourceId = _inputSourceId,
				MicrophoneActive = _microphoneActive,
				CameraActive = _cameraActive,
				Scroll = WindowLocked(_scroll, fromWallMsExclusive, toWallMsInclusive).ToArray(),
				Clicks = WindowLocked(_clicks, fromWallMsExclusive, toWallMsInclusive).ToArray(),
				MousePositions = WindowLocked(_mousePositions, fromWallMsExclusive, toWallMsInclusive).ToArray(),
				KeyTransitions = WindowLocked(_keyTransitions, fromWallMsExclusive, toWallMsInclusive).ToArray()
			};
		}
	}

	private static void AppendLocked<T>(List<T> list, T entry) where T : IHasTimestamp
	{
		if (list.Count > 0)
		{
			if (list[list.Count - 1].TimestampMs > entry.TimestampMs)
			{
				list.Insert(UpperBound(list, entry.TimestampMs), entry);
				goto IL_0057;
			}
		}
		list.Add(entry);
		goto IL_0057;
		IL_0057:
		PruneLocked(list, entry.TimestampMs - 5000);
	}

	private static void PruneLocked<T>(List<T> list, long cutoffMs) where T : IHasTimestamp
	{
		int num = LowerBound(list, cutoffMs);
		if (num > 0 && num < list.Count)
		{
			list.RemoveRange(0, num);
		}
	}

	private static ReadOnlySpan<T> WindowLocked<T>(List<T> list, long fromExclusive, long toInclusive) where T : IHasTimestamp
	{
		int num = UpperBound(list, fromExclusive);
		int num2 = UpperBound(list, toInclusive);
		if (num2 <= num)
		{
			return ReadOnlySpan<T>.Empty;
		}
		return CollectionsMarshal.AsSpan(list).Slice(num, num2 - num);
	}

	private static int LowerBound<T>(List<T> list, long timestampMs) where T : IHasTimestamp
	{
		int num = 0;
		int num2 = list.Count;
		while (num < num2)
		{
			int num3 = num + num2 >> 1;
			if (list[num3].TimestampMs < timestampMs)
			{
				num = num3 + 1;
			}
			else
			{
				num2 = num3;
			}
		}
		return num;
	}

	private static int UpperBound<T>(List<T> list, long timestampMs) where T : IHasTimestamp
	{
		int num = 0;
		int num2 = list.Count;
		while (num < num2)
		{
			int num3 = num + num2 >> 1;
			if (list[num3].TimestampMs <= timestampMs)
			{
				num = num3 + 1;
			}
			else
			{
				num2 = num3;
			}
		}
		return num;
	}
}
