using System;
using System.Diagnostics;
using System.Threading;

namespace KuroWindows.RecorderCore.Capture;

internal sealed class PipelineHeartbeat
{
	private readonly Func<long> _qpcNow;

	private long _lastBeatQpc;

	public PipelineHeartbeat()
		: this(Stopwatch.GetTimestamp)
	{
	}

	internal PipelineHeartbeat(Func<long> qpcNow)
	{
		_qpcNow = qpcNow;
		_lastBeatQpc = qpcNow();
	}

	public void Beat()
	{
		Interlocked.Exchange(ref _lastBeatQpc, _qpcNow());
	}

	public double SilenceSeconds(long nowQpc)
	{
		return (double)Math.Max(0L, nowQpc - Interlocked.Read(in _lastBeatQpc)) / (double)Stopwatch.Frequency;
	}
}
