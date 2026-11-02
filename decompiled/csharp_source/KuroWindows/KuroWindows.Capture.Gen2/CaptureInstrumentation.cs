using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;

namespace KuroWindows.Capture.Gen2;

internal sealed class CaptureInstrumentation
{
	private struct Snapshot
	{
		public long AcquireTimeout;

		public long AcquireDesktopUpdate;

		public long AcquireCursorOnly;

		public long AccumulatedFramesSum;

		public long AccumulatedFramesGt1;

		public long AccumulatedFramesMax;

		public long GateReject;

		public long RouterReject;

		public long Deliver;

		public long DeliverCursorOnly;

		public long MailboxOverwrite;

		public long DeliverCopyTicks;

		public long DeliverCopyCount;

		public long DeliverLockWaitTicks;

		public long EncodeCopyTicks;

		public long EncodeCopyCount;

		public long ConsumeSource;

		public long ConsumeDuplicate;
	}

	private const string EnvVar = "KURO_CAPTURE_INSTRUMENT";

	private static readonly long FlushIntervalQpc = Stopwatch.Frequency * 5;

	private long _acquireTimeout;

	private long _acquireDesktopUpdate;

	private long _acquireCursorOnly;

	private long _accumulatedFramesSum;

	private long _accumulatedFramesGt1;

	private long _accumulatedFramesMax;

	private long _gateReject;

	private long _routerReject;

	private long _deliver;

	private long _deliverCursorOnly;

	private long _mailboxOverwrite;

	private long _deliverCopyTicks;

	private long _deliverCopyCount;

	private long _deliverLockWaitTicks;

	private long _encodeCopyTicks;

	private long _encodeCopyCount;

	private long _consumeSource;

	private long _consumeDuplicate;

	private Snapshot _last;

	private long _lastFlushQpc;

	private bool _flushArmed;

	public static CaptureInstrumentation? CreateIfEnabled()
	{
		bool flag;
		switch (Environment.GetEnvironmentVariable("KURO_CAPTURE_INSTRUMENT")?.Trim().ToLowerInvariant())
		{
		case "1":
		case "true":
		case "yes":
		case "on":
			flag = true;
			break;
		default:
			flag = false;
			break;
		}
		if (!flag)
		{
			return null;
		}
		return new CaptureInstrumentation();
	}

	public void RecordAcquireOutcome(Gen2AcquireOutcome outcome, long accumulatedFrames)
	{
		switch (outcome)
		{
		case Gen2AcquireOutcome.Timeout:
			Interlocked.Increment(ref _acquireTimeout);
			break;
		case Gen2AcquireOutcome.CursorOnly:
			Interlocked.Increment(ref _acquireCursorOnly);
			break;
		case Gen2AcquireOutcome.DesktopUpdate:
			Interlocked.Increment(ref _acquireDesktopUpdate);
			if (accumulatedFrames > 1)
			{
				Interlocked.Increment(ref _accumulatedFramesGt1);
			}
			Interlocked.Add(ref _accumulatedFramesSum, Math.Max(accumulatedFrames, 0L));
			UpdateMax(ref _accumulatedFramesMax, accumulatedFrames);
			break;
		}
	}

	public void RecordGateReject()
	{
		Interlocked.Increment(ref _gateReject);
	}

	public void RecordRouterReject()
	{
		Interlocked.Increment(ref _routerReject);
	}

	public void RecordDeliver(bool overwroteUnconsumed, long copyTicks, long lockWaitTicks)
	{
		Interlocked.Increment(ref _deliver);
		if (overwroteUnconsumed)
		{
			Interlocked.Increment(ref _mailboxOverwrite);
		}
		Interlocked.Add(ref _deliverCopyTicks, copyTicks);
		Interlocked.Increment(ref _deliverCopyCount);
		Interlocked.Add(ref _deliverLockWaitTicks, lockWaitTicks);
	}

	public void RecordDeliverCursorOnly()
	{
		Interlocked.Increment(ref _deliverCursorOnly);
	}

	public void RecordEncodeCopy(long copyTicks)
	{
		Interlocked.Add(ref _encodeCopyTicks, copyTicks);
		Interlocked.Increment(ref _encodeCopyCount);
	}

	public void RecordConsume(bool fresh)
	{
		if (fresh)
		{
			Interlocked.Increment(ref _consumeSource);
		}
		else
		{
			Interlocked.Increment(ref _consumeDuplicate);
		}
	}

	public void MaybeFlush(long nowQpc, string frontEnd, int displayIndex, Action<string> log)
	{
		if (!_flushArmed)
		{
			_flushArmed = true;
			_lastFlushQpc = nowQpc;
			_last = Capture();
			return;
		}
		long num = nowQpc - _lastFlushQpc;
		double elapsedSec;
		if (num >= FlushIntervalQpc)
		{
			Snapshot last = Capture();
			elapsedSec = (double)num / (double)Stopwatch.Frequency;
			if (elapsedSec <= 0.0)
			{
				elapsedSec = 1.0;
			}
			long num2 = last.AcquireDesktopUpdate - _last.AcquireDesktopUpdate;
			long num3 = last.AccumulatedFramesSum - _last.AccumulatedFramesSum;
			double num4 = ((num2 > 0) ? ((double)num3 / (double)num2) : 0.0);
			log(string.Format(CultureInfo.InvariantCulture, "[capture-instrument] frontend={0} display={1} window={2:F1}s | acquire/s desk={3:F1} cursor={4:F1} timeout={5:F1} | accumframes mean={6:F2} max={7} coalesced_gt1={8} | gate_reject={9} router_reject={10} deliver/s={11:F1} cursor_deliver/s={12:F1} mailbox_overwrite={13} | deliver_copy_ms={14:F2} deliver_lockwait_ms={15:F2} encode_copy_ms={16:F2} | consume src={17} dup={18}", frontEnd, displayIndex, elapsedSec, Rate(last.AcquireDesktopUpdate, _last.AcquireDesktopUpdate), Rate(last.AcquireCursorOnly, _last.AcquireCursorOnly), Rate(last.AcquireTimeout, _last.AcquireTimeout), num4, last.AccumulatedFramesMax, Delta(last.AccumulatedFramesGt1, _last.AccumulatedFramesGt1), Delta(last.GateReject, _last.GateReject), Delta(last.RouterReject, _last.RouterReject), Rate(last.Deliver, _last.Deliver), Rate(last.DeliverCursorOnly, _last.DeliverCursorOnly), Delta(last.MailboxOverwrite, _last.MailboxOverwrite), AvgMs(last.DeliverCopyTicks, _last.DeliverCopyTicks, last.DeliverCopyCount, _last.DeliverCopyCount), AvgMs(last.DeliverLockWaitTicks, _last.DeliverLockWaitTicks, last.DeliverCopyCount, _last.DeliverCopyCount), AvgMs(last.EncodeCopyTicks, _last.EncodeCopyTicks, last.EncodeCopyCount, _last.EncodeCopyCount), Delta(last.ConsumeSource, _last.ConsumeSource), Delta(last.ConsumeDuplicate, _last.ConsumeDuplicate)));
			_last = last;
			_lastFlushQpc = nowQpc;
		}
		static double AvgMs(long ticksCur, long ticksPrev, long countCur, long countPrev)
		{
			long num5 = countCur - countPrev;
			if (num5 > 0)
			{
				return (double)(ticksCur - ticksPrev) * 1000.0 / (double)Stopwatch.Frequency / (double)num5;
			}
			return 0.0;
		}
		static long Delta(long cur, long prev)
		{
			return cur - prev;
		}
		double Rate(long cur, long prev)
		{
			return (double)(cur - prev) / elapsedSec;
		}
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

	private Snapshot Capture()
	{
		return new Snapshot
		{
			AcquireTimeout = Interlocked.Read(in _acquireTimeout),
			AcquireDesktopUpdate = Interlocked.Read(in _acquireDesktopUpdate),
			AcquireCursorOnly = Interlocked.Read(in _acquireCursorOnly),
			AccumulatedFramesSum = Interlocked.Read(in _accumulatedFramesSum),
			AccumulatedFramesGt1 = Interlocked.Read(in _accumulatedFramesGt1),
			AccumulatedFramesMax = Interlocked.Read(in _accumulatedFramesMax),
			GateReject = Interlocked.Read(in _gateReject),
			RouterReject = Interlocked.Read(in _routerReject),
			Deliver = Interlocked.Read(in _deliver),
			DeliverCursorOnly = Interlocked.Read(in _deliverCursorOnly),
			MailboxOverwrite = Interlocked.Read(in _mailboxOverwrite),
			DeliverCopyTicks = Interlocked.Read(in _deliverCopyTicks),
			DeliverCopyCount = Interlocked.Read(in _deliverCopyCount),
			DeliverLockWaitTicks = Interlocked.Read(in _deliverLockWaitTicks),
			EncodeCopyTicks = Interlocked.Read(in _encodeCopyTicks),
			EncodeCopyCount = Interlocked.Read(in _encodeCopyCount),
			ConsumeSource = Interlocked.Read(in _consumeSource),
			ConsumeDuplicate = Interlocked.Read(in _consumeDuplicate)
		};
	}
}
