using System;
using System.Threading;

namespace KuroWindows.RecorderCore.Diagnostics;

internal sealed class LatencyHistogram
{
	private static readonly long[] EdgesMs = new long[13]
	{
		1L, 2L, 4L, 8L, 16L, 33L, 66L, 100L, 250L, 500L,
		1000L, 2500L, 5000L
	};

	private readonly long[] _bucketCounts = new long[EdgesMs.Length + 1];

	private long _count;

	private long _sumMs;

	private long _maxMs;

	public long Count => Interlocked.Read(in _count);

	public long MaxMs => Interlocked.Read(in _maxMs);

	public long MeanMs
	{
		get
		{
			long num = Interlocked.Read(in _count);
			if (num <= 0)
			{
				return 0L;
			}
			return Interlocked.Read(in _sumMs) / num;
		}
	}

	public void Record(long valueMs)
	{
		if (valueMs < 0)
		{
			valueMs = 0L;
		}
		Interlocked.Increment(ref _bucketCounts[BucketIndex(valueMs)]);
		Interlocked.Increment(ref _count);
		Interlocked.Add(ref _sumMs, valueMs);
		UpdateMax(ref _maxMs, valueMs);
	}

	public long CountAtOrAbove(long thresholdMs)
	{
		long num = 0L;
		for (int i = 0; i <= EdgesMs.Length; i++)
		{
			if (((i == 0) ? 0 : EdgesMs[i - 1]) >= thresholdMs)
			{
				num += Interlocked.Read(in _bucketCounts[i]);
			}
		}
		return num;
	}

	public long QuantileMs(double quantile)
	{
		long num = Interlocked.Read(in _count);
		if (num <= 0)
		{
			return 0L;
		}
		long num2 = Math.Clamp((long)Math.Ceiling(quantile * (double)num), 1L, num);
		long num3 = 0L;
		for (int i = 0; i <= EdgesMs.Length; i++)
		{
			long num4 = Interlocked.Read(in _bucketCounts[i]);
			if (num3 + num4 < num2)
			{
				num3 += num4;
				continue;
			}
			long num5 = ((i == 0) ? 0 : EdgesMs[i - 1]);
			long num6 = ((i == EdgesMs.Length) ? Math.Max(Interlocked.Read(in _maxMs), num5) : EdgesMs[i]);
			double num7 = ((num4 > 0) ? ((double)(num2 - num3) / (double)num4) : 0.0);
			return Math.Min((long)Math.Round((double)num5 + num7 * (double)(num6 - num5)), Interlocked.Read(in _maxMs));
		}
		return Interlocked.Read(in _maxMs);
	}

	private static int BucketIndex(long valueMs)
	{
		for (int i = 0; i < EdgesMs.Length; i++)
		{
			if (valueMs < EdgesMs[i])
			{
				return i;
			}
		}
		return EdgesMs.Length;
	}

	private static void UpdateMax(ref long target, long value)
	{
		long num;
		do
		{
			num = Interlocked.Read(in target);
		}
		while (value > num && Interlocked.CompareExchange(ref target, value, num) != num);
	}
}
