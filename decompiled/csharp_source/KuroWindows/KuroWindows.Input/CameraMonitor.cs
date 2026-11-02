namespace KuroWindows.Input;

internal sealed class CameraMonitor : CapabilityUsageMonitor
{
	public CameraMonitor()
		: base("webcam", "camera_status", AppConfig.CapabilityPrivacyPollIntervalMs)
	{
	}
}
