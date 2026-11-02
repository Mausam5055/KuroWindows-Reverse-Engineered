using KuroWindows.RecorderCore.Clock;

namespace KuroWindows.RecorderCore.Capture;

internal readonly record struct PadFrameStamp(long TimestampTicks, long WallMs, TimestampKind TimestampKind, TimestampQuality TimestampQuality, CaptureFrameKind FrameKind);
