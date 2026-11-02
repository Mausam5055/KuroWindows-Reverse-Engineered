using System.Collections.Generic;

namespace KuroWindows.Upload;

internal record EventScanResult(long IdleDurationMs, int Clicks, int Keystrokes, int Scrolls, IReadOnlyDictionary<string, double> AppUsage, string? SegmentEndReason, string? SegmentMaskTrigger, string? SegmentMaskDetail, int SyntheticEventCount, int TotalInputEventCount, double? TimingCv, bool JigglerDetected, string? JigglerAppName)
{
	public static EventScanResult Empty => new EventScanResult(0L, 0, 0, 0, new Dictionary<string, double>(), null, null, null, 0, 0, null, JigglerDetected: false, null);
}
