namespace KuroWindows.Capture;

internal sealed record SegmentEndItem(SegmentSet Set, long[] Timestamps, string Reason, string? MaskTrigger, string? MaskDetail, SealBarrier Barrier);
