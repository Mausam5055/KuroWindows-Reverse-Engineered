using KuroWindows.RecorderCore.Capture;
using KuroWindows.RecorderCore.Clock;

namespace KuroWindows.Capture;

internal readonly record struct FrameCommitRequest(int DisplayIndex, byte[] BgraData, long SourceQpc, long WallMs, long SourceCaptureWallMs, TimestampKind TimestampKind, TimestampQuality TimestampQuality, CaptureFrameKind FrameKind, CaptureBackendKind Backend, ulong FrameVisualHash, int? FrameChangeScore, CursorFrameSample Cursor, int CursorScreen)
{
	public int GpuSampleSlot { get; init; } = -1;

	public int FramesDroppedSincePrev { get; init; } = 0;

	public bool? CursorBlendChanged { get; init; } = null;
}
