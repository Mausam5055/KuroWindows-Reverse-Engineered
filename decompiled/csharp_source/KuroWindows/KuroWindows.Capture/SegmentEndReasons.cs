using KuroWindows.Auth;

namespace KuroWindows.Capture;

internal static class SegmentEndReasons
{
	public const string UserStop = "user_stop";

	public const string TimerRotation = "timer_rotation";

	public const string Idle = "idle";

	public const string SystemSleep = "system_sleep";

	public const string ScreenLock = "screen_lock";

	public const string SessionLogoff = "session_logoff";

	public const string SessionDisconnect = "session_disconnect";

	public const string PrivacyMask = "privacy_mask";

	public const string LowDisk = "low_disk";

	public const string ForceUpdate = "force_update";

	public const string CaptureFailure = "capture_failure";

	public const string CaptureAccessDenied = "capture_access_denied";

	public const string AppQuit = "app_quit";

	public const string TopologyChange = "topology_change";

	public const string UnsupportedDisplayTopology = "unsupported_display_topology";

	public const string RecordingNotExpected = "recording_not_expected";

	public const string DesktopSyncAccessDenied = "desktop_sync_access_denied";

	public const string TriggerOsLockScreen = "os_lock_screen";

	public const string TriggerCaptureContention = "capture_contention";

	public static string Auth(AuthState state)
	{
		return "auth_" + state.ToString().ToLowerInvariant();
	}

	public static bool AudioSealUsesFloor(string endReason)
	{
		if (endReason == "app_quit" || endReason == "topology_change")
		{
			return true;
		}
		return false;
	}
}
