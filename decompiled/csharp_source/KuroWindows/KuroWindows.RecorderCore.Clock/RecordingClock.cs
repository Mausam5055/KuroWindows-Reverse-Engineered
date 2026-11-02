using System;
using System.Diagnostics;
using System.Threading;
using ClientTelemetry;

namespace KuroWindows.RecorderCore.Clock;

public sealed class RecordingClock
{
	private sealed record Anchor(long Qpc, long WallMs);

	private static readonly double TicksToMs = 1000.0 / (double)Stopwatch.Frequency;

	private static readonly long ProcessStartQpc = Stopwatch.GetTimestamp();

	private const long RelatchLogThresholdMs = 1000L;

	public static readonly RecordingClock Shared = new RecordingClock(Stopwatch.GetTimestamp, () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

	private readonly Func<long> _qpcNow;

	private readonly Func<long> _wallNowMs;

	private Anchor _anchor;

	internal RecordingClock(Func<long> qpcNow, Func<long> wallNowMs)
	{
		_qpcNow = qpcNow;
		_wallNowMs = wallNowMs;
		_anchor = new Anchor(qpcNow(), wallNowMs());
	}

	public double NowMs()
	{
		return WallMsFromQpc(_qpcNow());
	}

	public long WallMsFromQpc(long qpcTicks)
	{
		Anchor anchor = Volatile.Read(in _anchor);
		return anchor.WallMs + (long)Math.Round((double)(qpcTicks - anchor.Qpc) * TicksToMs);
	}

	public long QpcFromWallMs(long wallMs)
	{
		Anchor anchor = Volatile.Read(in _anchor);
		return anchor.Qpc + (long)Math.Round((double)(wallMs - anchor.WallMs) / TicksToMs);
	}

	public long Relatch(string reason)
	{
		long num = _qpcNow();
		long num2 = _wallNowMs();
		Anchor anchor = Interlocked.Exchange(ref _anchor, new Anchor(num, num2));
		long num3 = num2 - (anchor.WallMs + (long)Math.Round((double)(num - anchor.Qpc) * TicksToMs));
		if (Math.Abs(num3) >= 1000)
		{
			KuroLog.Capture($"recording_clock relatch reason={reason} correction_ms={num3}", "Relatch", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\RecorderCore\\Clock\\RecordingClock.cs", 92);
			Telemetry.Emit(Ev.ClockJumped(num3, 0L, ClockJumpSource.WakeRelatch));
		}
		return num3;
	}

	public static long MonotonicMs()
	{
		return (long)Math.Round((double)(Stopwatch.GetTimestamp() - ProcessStartQpc) * TicksToMs);
	}

	public static long MsBetween(long fromQpc, long toQpc)
	{
		return (toQpc - fromQpc) * 1000 / Stopwatch.Frequency;
	}
}
