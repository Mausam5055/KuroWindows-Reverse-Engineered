namespace KuroWindows.Input;

internal sealed class MicrophoneMonitor : CapabilityUsageMonitor
{
	public MicrophoneMonitor()
		: base("microphone", "microphone_status", AppConfig.CapabilityPollIntervalMs)
	{
	}
}
