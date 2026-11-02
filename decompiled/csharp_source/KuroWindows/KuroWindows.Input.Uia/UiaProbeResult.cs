namespace KuroWindows.Input.Uia;

internal readonly record struct UiaProbeResult(UiaProbeStatus Status, bool IsSecure, string? SelectedText)
{
	internal static UiaProbeResult Unavailable => new UiaProbeResult(UiaProbeStatus.Unavailable, IsSecure: false, null);
}
