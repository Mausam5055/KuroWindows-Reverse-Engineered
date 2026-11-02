namespace KuroWindows.Input.Touchpad;

internal sealed record TouchpadSettingsSnapshot(string Source, bool? Present, bool? LegacyPresent, bool? ExternalMouse, bool? Enabled, bool? Active, bool Pan, bool Zoom, bool Reversed, int? MaxContacts, int? Sensitivity, int? CursorSpeed, bool Tap, bool TwoFingerTap, bool LeaveOnWithMouse)
{
	public object ToBlock()
	{
		return new
		{
			source = Source,
			present = Present,
			legacyPresent = LegacyPresent,
			externalMouse = ExternalMouse,
			enabled = Enabled,
			active = Active,
			pan = Pan,
			zoom = Zoom,
			reversed = Reversed,
			maxContacts = MaxContacts,
			sensitivity = Sensitivity,
			cursorSpeed = CursorSpeed,
			tap = Tap,
			twoFingerTap = TwoFingerTap,
			leaveOnWithMouse = LeaveOnWithMouse
		};
	}
}
