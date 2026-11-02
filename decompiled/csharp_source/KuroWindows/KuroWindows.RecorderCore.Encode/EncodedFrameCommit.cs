using KuroWindows.RecorderCore.Capture;
using KuroWindows.RecorderCore.Clock;

namespace KuroWindows.RecorderCore.Encode;

public readonly record struct EncodedFrameCommit(long FrameIndex, long PtsNs, long DurationNs, long WallMs, CaptureFrameKind FrameKind, TimestampQuality Quality, TimestampKind TimestampKind, CaptureBackendKind Backend, long EncodeElapsedMs);
