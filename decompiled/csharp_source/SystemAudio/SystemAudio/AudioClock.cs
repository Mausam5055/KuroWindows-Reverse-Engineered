using System;
using System.Diagnostics;

namespace SystemAudio;

internal readonly struct AudioClock(long qpc0)
{
	public const int SampleRate = 48000;

	private readonly long _qpc0Hns = (long)((Int128)qpc0 * (Int128)10000000 / Stopwatch.Frequency);

	public long Qpc0 { get; } = qpc0;

	public long NowIndex => IndexFromQpc(Stopwatch.GetTimestamp());

	public long IndexFromQpc(long ticks)
	{
		long frequency = Stopwatch.Frequency;
		return (long)(((Int128)(ticks - Qpc0) * (Int128)48000 + frequency / 2) / frequency);
	}

	public long IndexFromQpcPositionHns(ulong positionHns)
	{
		return (((long)positionHns - _qpc0Hns) * 3 + 312) / 625;
	}

	public static long FramesToMs(long frames)
	{
		return frames * 1000 / 48000;
	}

	public static long MsToFrames(long ms)
	{
		return ms * 48000 / 1000;
	}
}
