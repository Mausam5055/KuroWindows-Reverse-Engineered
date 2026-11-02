using System;
using System.Threading;

namespace KuroWindows.Capture;

internal sealed class SealBarrier
{
	public SegmentSet Set { get; }

	public ManualResetEventSlim Done { get; } = new ManualResetEventSlim(initialState: false);

	public Exception? Error { get; set; }

	public SealBarrier(SegmentSet set)
	{
		Set = set;
	}
}
