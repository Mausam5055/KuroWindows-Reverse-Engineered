namespace KuroWindows.Capture;

internal sealed class BlindInterval
{
	public string Reason { get; }

	public string AppName { get; }

	public int Pid { get; }

	public SegmentSet? ReportedIn { get; set; }

	public long ReportedFrom { get; set; }

	public BlindInterval(string reason, string appName, int pid)
	{
		Reason = reason;
		AppName = appName;
		Pid = pid;
	}
}
