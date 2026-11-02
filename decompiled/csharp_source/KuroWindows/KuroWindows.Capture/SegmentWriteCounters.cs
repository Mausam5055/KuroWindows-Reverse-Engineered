namespace KuroWindows.Capture;

internal sealed class SegmentWriteCounters
{
	public long Late;

	public long Dropped;

	public long Thinned;

	public long BlindMs;

	public int QueuePeak;

	public bool Witness;

	public long ThrottleMs;

	public long ThrottleEvalMs;

	public long LastThinWallMs;

	public long ThinRows;

	public long ThinRowBytes;
}
