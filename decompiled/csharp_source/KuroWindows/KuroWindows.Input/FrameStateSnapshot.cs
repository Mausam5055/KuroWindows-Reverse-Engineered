using System;

namespace KuroWindows.Input;

public sealed class FrameStateSnapshot
{
	public bool LeftDown;

	public bool RightDown;

	public bool MiddleDown;

	public bool Other3Down;

	public bool Other4Down;

	public uint[] KeysDown = Array.Empty<uint>();

	public uint Modifiers;

	public ActiveAppSnapshot? ActiveApp;

	public string? Appearance;

	public int? BatteryLevel;

	public bool? IsCharging;

	public bool? IsOnAc;

	public bool? IsOnline;

	public string? NetworkInterfaceType;

	public string? MemoryPressure;

	public string? InputSourceName;

	public string? InputSourceId;

	public bool MicrophoneActive;

	public bool CameraActive;

	public ScrollEntry[] Scroll = Array.Empty<ScrollEntry>();

	public ClickEntry[] Clicks = Array.Empty<ClickEntry>();

	public MousePositionEntry[] MousePositions = Array.Empty<MousePositionEntry>();

	public KeyTransitionEntry[] KeyTransitions = Array.Empty<KeyTransitionEntry>();
}
