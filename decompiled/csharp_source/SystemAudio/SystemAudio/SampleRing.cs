using System;
using System.Threading;

namespace SystemAudio;

internal sealed class SampleRing
{
	public const int RingFrames = 96000;

	public const int ChannelCount = 2;

	public const int FeedMarginMs = 250;

	public const int FeedMarginSamples = 12000;

	public const int LeadClampSamples = 12000;

	private const int SlackSamples = 96;

	private const int CorrectLaterSamples = 480;

	private const int CorrectLaterDeadlineSamples = 48000;

	private const int LowRmsPeak = 512;

	private readonly AudioClock _clock;

	private readonly short[] _pcm = new short[192000];

	private long _producedUpTo;

	private long _consumedUpTo;

	private long _firstProduced = -1L;

	private long _pendingSince = -1L;

	private bool _overlapPending;

	private long _zerosFilled;

	private long _samplesDropped;

	private long _corrections;

	private long _maxAbsErrorUs;

	private long _droppedFrames;

	private long _discontinuities;

	private long _timestampErrors;

	public long ProducedUpTo => Volatile.Read(in _producedUpTo);

	public bool HasProducedAny => Volatile.Read(in _firstProduced) >= 0;

	public long ZerosFilled => Interlocked.Read(in _zerosFilled);

	public long SamplesDropped => Interlocked.Read(in _samplesDropped);

	public long Corrections => Interlocked.Read(in _corrections);

	public long MaxAbsErrorUs => Interlocked.Read(in _maxAbsErrorUs);

	public long DroppedMs => AudioClock.FramesToMs(Interlocked.Read(in _droppedFrames));

	public long Discontinuities => Interlocked.Read(in _discontinuities);

	public long TimestampErrors => Interlocked.Read(in _timestampErrors);

	public SampleRing(AudioClock clock)
	{
		_clock = clock;
	}

	public void Place(long stampIndex, ReadOnlySpan<short> frames, uint flags, bool afterReopen)
	{
		int num = frames.Length / 2;
		if (num <= 0)
		{
			return;
		}
		bool flag = (flags & 2) != 0;
		long nowIndex = _clock.NowIndex;
		bool num2 = (flags & 4) != 0 || stampIndex > nowIndex + 12000;
		long producedUpTo = _producedUpTo;
		long position;
		if (num2)
		{
			Interlocked.Increment(ref _timestampErrors);
			position = (HasProducedAny ? producedUpTo : (nowIndex - num));
		}
		else if (!HasProducedAny)
		{
			position = stampIndex;
		}
		else
		{
			long num3 = stampIndex - producedUpTo;
			long num4 = Math.Abs(num3);
			RecordMaxError(num4);
			bool flag2 = (flags & 1) != 0;
			if (flag2)
			{
				Interlocked.Increment(ref _discontinuities);
			}
			bool flag3 = flag2 | afterReopen;
			if (!flag3)
			{
				if (num4 < 96)
				{
					_pendingSince = -1L;
				}
				else if (num4 < 480)
				{
					if (_pendingSince < 0)
					{
						_pendingSince = producedUpTo;
					}
					flag3 = flag || Peak(frames) < 512 || producedUpTo - _pendingSince >= 48000;
				}
				else
				{
					flag3 = true;
				}
			}
			if (flag3 && num3 != 0L)
			{
				if (num3 > 0)
				{
					if (FillZeros(producedUpTo, stampIndex))
					{
						Interlocked.Increment(ref _corrections);
					}
					position = stampIndex;
				}
				else
				{
					int num5 = (int)Math.Min(-num3, num);
					Interlocked.Add(ref _samplesDropped, num5);
					frames = frames.Slice(num5 * 2);
					num -= num5;
					position = producedUpTo;
					_overlapPending = true;
				}
			}
			else
			{
				position = producedUpTo;
			}
			if (flag3)
			{
				_pendingSince = -1L;
			}
			if (_overlapPending && num > 0)
			{
				_overlapPending = false;
				Interlocked.Increment(ref _corrections);
			}
		}
		Write(position, frames, num, flag);
	}

	public int Read(long fromIndex, Span<short> dst, int frames)
	{
		long num = Volatile.Read(in _producedUpTo);
		long num2 = Volatile.Read(in _firstProduced);
		long num3 = fromIndex + frames;
		long num4 = Math.Max(fromIndex, Math.Max((num2 < 0) ? long.MaxValue : num2, num - 96000));
		long num5 = Math.Min(num3, num);
		int num6 = 0;
		if (num4 >= num5)
		{
			dst.Slice(0, frames * 2).Clear();
			num6 = frames;
		}
		else
		{
			int num7 = (int)(num4 - fromIndex);
			int num8 = (int)(num3 - num5);
			if (num7 > 0)
			{
				dst.Slice(0, num7 * 2).Clear();
			}
			int num9;
			if (num8 > 0)
			{
				num9 = (frames - num8) * 2;
				dst.Slice(num9, frames * 2 - num9).Clear();
			}
			num9 = num7 * 2;
			CopyOut(num4, dst.Slice(num9, (frames - num8) * 2 - num9));
			num6 = num7 + num8;
		}
		AdvanceConsumed(num3);
		return num6;
	}

	public void AdvanceConsumed(long upTo)
	{
		if (upTo > Volatile.Read(in _consumedUpTo))
		{
			Volatile.Write(ref _consumedUpTo, upTo);
		}
	}

	public void ResetMaxAbsError()
	{
		Interlocked.Exchange(ref _maxAbsErrorUs, 0L);
	}

	private void Write(long position, ReadOnlySpan<short> frames, int count, bool silent)
	{
		long num = Volatile.Read(in _consumedUpTo);
		if (position < num)
		{
			ClearPassed(position, num);
			int num2 = (int)Math.Min(num - position, count);
			if (!silent)
			{
				Interlocked.Add(ref _samplesDropped, num2);
			}
			position += num2;
			frames = frames.Slice(num2 * 2);
			count -= num2;
		}
		int num3 = (int)Math.Max(0L, Math.Min(96000 - (position - num), 2147483647L));
		if (count > num3)
		{
			Interlocked.Add(ref _droppedFrames, count - num3);
			count = num3;
		}
		if (count > 0)
		{
			if (silent)
			{
				ClearSlots(position, count);
			}
			else
			{
				CopyIn(position, frames.Slice(0, count * 2));
			}
			if (Volatile.Read(in _firstProduced) < 0)
			{
				Volatile.Write(ref _firstProduced, position);
			}
			AdvanceProduced(position + count);
		}
	}

	private bool FillZeros(long from, long to)
	{
		long num = Volatile.Read(in _consumedUpTo);
		if (from < num)
		{
			ClearPassed(from, num);
			from = num;
		}
		to = Math.Min(to, num + 96000);
		if (to <= from)
		{
			return false;
		}
		Interlocked.Add(ref _zerosFilled, to - from);
		ClearSlots(from, (int)(to - from));
		AdvanceProduced(to);
		return true;
	}

	private void ClearPassed(long from, long upTo)
	{
		from = Math.Max(from, upTo - 96000);
		if (upTo > from)
		{
			ClearSlots(from, (int)(upTo - from));
		}
	}

	private void AdvanceProduced(long upTo)
	{
		if (upTo > _producedUpTo)
		{
			Volatile.Write(ref _producedUpTo, upTo);
		}
	}

	private void RecordMaxError(long absErrFrames)
	{
		long num = absErrFrames * 1000000 / 48000;
		long num2;
		do
		{
			num2 = Interlocked.Read(in _maxAbsErrorUs);
		}
		while (num > num2 && Interlocked.CompareExchange(ref _maxAbsErrorUs, num, num2) != num2);
	}

	private static int Peak(ReadOnlySpan<short> frames)
	{
		int num = 0;
		ReadOnlySpan<short> readOnlySpan = frames;
		for (int i = 0; i < readOnlySpan.Length; i++)
		{
			int num2 = Math.Abs((int)readOnlySpan[i]);
			if (num2 > num)
			{
				num = num2;
			}
		}
		return num;
	}

	private void CopyIn(long position, ReadOnlySpan<short> samples)
	{
		int num = (int)(position % 96000) * 2;
		int num2 = Math.Min(samples.Length, _pcm.Length - num);
		samples.Slice(0, num2).CopyTo(_pcm.AsSpan(num, num2));
		if (num2 < samples.Length)
		{
			samples.Slice(num2).CopyTo(_pcm.AsSpan(0, samples.Length - num2));
		}
	}

	private void CopyOut(long position, Span<short> dst)
	{
		int num = (int)(position % 96000) * 2;
		int num2 = Math.Min(dst.Length, _pcm.Length - num);
		_pcm.AsSpan(num, num2).CopyTo(dst.Slice(0, num2));
		if (num2 < dst.Length)
		{
			_pcm.AsSpan(0, dst.Length - num2).CopyTo(dst.Slice(num2));
		}
	}

	private void ClearSlots(long position, int count)
	{
		int num = (int)(position % 96000) * 2;
		int num2 = count * 2;
		int num3 = Math.Min(num2, _pcm.Length - num);
		_pcm.AsSpan(num, num3).Clear();
		if (num3 < num2)
		{
			_pcm.AsSpan(0, num2 - num3).Clear();
		}
	}
}
