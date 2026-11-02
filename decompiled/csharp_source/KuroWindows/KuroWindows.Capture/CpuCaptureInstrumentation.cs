using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;

namespace KuroWindows.Capture;

internal sealed class CpuCaptureInstrumentation
{
	internal sealed class PipelineWindow
	{
		private readonly object _gate = new object();

		private Snapshot? _end;

		private long _endQpc;

		internal Snapshot Baseline { get; }

		internal long StartQpc { get; }

		internal (Snapshot End, long EndQpc) Ended
		{
			get
			{
				lock (_gate)
				{
					return (End: _end.Value, EndQpc: _endQpc);
				}
			}
		}

		internal PipelineWindow(Snapshot baseline, long startQpc)
		{
			Baseline = baseline;
			StartQpc = startQpc;
		}

		internal void CloseAt(Snapshot end, long endQpc)
		{
			lock (_gate)
			{
				Snapshot? end2 = _end;
				if (!end2.HasValue)
				{
					_end = end;
					_endQpc = endQpc;
				}
			}
		}
	}

	internal struct Snapshot
	{
		public long AcquireNone;

		public long AcquireDesktopUpdate;

		public long AcquireCursorOnly;

		public long AccumulatedFramesSum;

		public long AccumulatedFramesGt1;

		public long AccumulatedFramesMax;

		public long StaleAcquireGe1S;

		public long StaleAcquireGe10S;

		public long StaleAcquireGe120S;

		public long RouterRejectPreReadback;

		public long GateSkip;

		public long StagingCopyTicks;

		public long MapWaitTicks;

		public long MapWaitMaxTicks;

		public long ReadbackCopyTicks;

		public long ReadbackCount;

		public long PublishCount;

		public long PublishLockWaitTicks;

		public long PublishLockWaitMaxTicks;

		public long MailboxOverwrite;

		public long CursorOnlyPublish;

		public long CursorOnlyClobber;

		public long ConsumeFresh;

		public long ConsumePad;

		public long ConsumeLockWaitTicks;

		public long ConsumeCopyTicks;

		public long ConsumeCopyCount;

		public long RejectAfterConsumeWindow;

		public long RejectAfterConsumeWindowSource;

		public long RejectAfterConsumeCommit;

		public long RejectAfterConsumeCommitSource;

		public long Commits;
	}

	private const string EnvVar = "KURO_CAPTURE_INSTRUMENT";

	private static readonly long DetailFlushIntervalQpc = Stopwatch.Frequency * 5;

	private readonly bool _detailEnabled;

	private long _acquireNone;

	private long _acquireDesktopUpdate;

	private long _acquireCursorOnly;

	private long _accumulatedFramesSum;

	private long _accumulatedFramesGt1;

	private long _accumulatedFramesMax;

	private long _lastDesktopUpdateQpc;

	private long _staleAcquireGe1S;

	private long _staleAcquireGe10S;

	private long _staleAcquireGe120S;

	private long _routerRejectPreReadback;

	private long _gateSkip;

	private long _stagingCopyTicks;

	private long _mapWaitTicks;

	private long _mapWaitMaxTicks;

	private long _readbackCopyTicks;

	private long _readbackCount;

	private long _publishCount;

	private long _publishLockWaitTicks;

	private long _publishLockWaitMaxTicks;

	private long _mailboxOverwrite;

	private long _cursorOnlyPublish;

	private long _cursorOnlyClobber;

	private long _consumeFresh;

	private long _consumePad;

	private long _consumeLockWaitTicks;

	private long _consumeCopyTicks;

	private long _consumeCopyCount;

	private long _rejectAfterConsumeWindow;

	private long _rejectAfterConsumeWindowSource;

	private long _rejectAfterConsumeCommit;

	private long _rejectAfterConsumeCommitSource;

	private long _commits;

	private Snapshot _lastDetail;

	private long _lastDetailFlushQpc;

	private bool _detailArmed;

	private PipelineWindow? _openWindow;

	public CpuCaptureInstrumentation()
	{
		bool detailEnabled;
		switch (Environment.GetEnvironmentVariable("KURO_CAPTURE_INSTRUMENT")?.Trim().ToLowerInvariant())
		{
		case "1":
		case "true":
		case "yes":
		case "on":
			detailEnabled = true;
			break;
		default:
			detailEnabled = false;
			break;
		}
		_detailEnabled = detailEnabled;
	}

	public void RecordAcquireNone(long nowQpc)
	{
		Interlocked.Increment(ref _acquireNone);
		long num = Interlocked.Read(in _lastDesktopUpdateQpc);
		if (num != 0L)
		{
			long num2 = nowQpc - num;
			if (num2 >= Stopwatch.Frequency)
			{
				Interlocked.Increment(ref _staleAcquireGe1S);
			}
			if (num2 >= Stopwatch.Frequency * 10)
			{
				Interlocked.Increment(ref _staleAcquireGe10S);
			}
			if (num2 >= Stopwatch.Frequency * 120)
			{
				Interlocked.Increment(ref _staleAcquireGe120S);
			}
		}
	}

	public void RecordAcquireCursorOnly()
	{
		Interlocked.Increment(ref _acquireCursorOnly);
	}

	public void RecordAcquireDesktopUpdate(long accumulatedFrames, long acquisitionQpc)
	{
		Interlocked.Increment(ref _acquireDesktopUpdate);
		Interlocked.Add(ref _accumulatedFramesSum, accumulatedFrames);
		if (accumulatedFrames > 1)
		{
			Interlocked.Increment(ref _accumulatedFramesGt1);
		}
		UpdateMax(ref _accumulatedFramesMax, accumulatedFrames);
		Interlocked.Exchange(ref _lastDesktopUpdateQpc, acquisitionQpc);
	}

	public void RecordRouterRejectPreReadback()
	{
		Interlocked.Increment(ref _routerRejectPreReadback);
	}

	public void RecordGateSkip()
	{
		Interlocked.Increment(ref _gateSkip);
	}

	public void RecordReadbackPhases(long stagingCopyTicks, long mapWaitTicks, long readbackCopyTicks)
	{
		Interlocked.Add(ref _stagingCopyTicks, stagingCopyTicks);
		Interlocked.Add(ref _mapWaitTicks, mapWaitTicks);
		UpdateMax(ref _mapWaitMaxTicks, mapWaitTicks);
		Interlocked.Add(ref _readbackCopyTicks, readbackCopyTicks);
		Interlocked.Increment(ref _readbackCount);
	}

	public void RecordPublish(bool overwrotePendingFrame, long lockWaitTicks)
	{
		Interlocked.Increment(ref _publishCount);
		Interlocked.Add(ref _publishLockWaitTicks, lockWaitTicks);
		UpdateMax(ref _publishLockWaitMaxTicks, lockWaitTicks);
		if (overwrotePendingFrame)
		{
			Interlocked.Increment(ref _mailboxOverwrite);
		}
	}

	public void RecordCursorOnlyPublish(bool clobberedPendingSourceFrame)
	{
		Interlocked.Increment(ref _cursorOnlyPublish);
		if (clobberedPendingSourceFrame)
		{
			Interlocked.Increment(ref _cursorOnlyClobber);
		}
	}

	public void RecordConsume(bool freshFrame, long lockWaitTicks, long blockCopyTicks)
	{
		if (freshFrame)
		{
			Interlocked.Increment(ref _consumeFresh);
		}
		else
		{
			Interlocked.Increment(ref _consumePad);
		}
		Interlocked.Add(ref _consumeLockWaitTicks, lockWaitTicks);
		Interlocked.Add(ref _consumeCopyTicks, blockCopyTicks);
		Interlocked.Increment(ref _consumeCopyCount);
	}

	public void RecordRejectAfterConsume(bool atCommit, bool wasSourceFrame)
	{
		if (atCommit)
		{
			Interlocked.Increment(ref _rejectAfterConsumeCommit);
			if (wasSourceFrame)
			{
				Interlocked.Increment(ref _rejectAfterConsumeCommitSource);
			}
		}
		else
		{
			Interlocked.Increment(ref _rejectAfterConsumeWindow);
			if (wasSourceFrame)
			{
				Interlocked.Increment(ref _rejectAfterConsumeWindowSource);
			}
		}
	}

	public void RecordCommit()
	{
		Interlocked.Increment(ref _commits);
	}

	public PipelineWindow OpenWindow(long nowQpc)
	{
		PipelineWindow pipelineWindow = new PipelineWindow(Capture(), nowQpc);
		Interlocked.Exchange(ref _openWindow, pipelineWindow)?.CloseAt(pipelineWindow.Baseline, nowQpc);
		return pipelineWindow;
	}

	public void FlushWindow(PipelineWindow window, long nowQpc, int displayIndex, Action<string> log)
	{
		window.CloseAt(Capture(), nowQpc);
		Interlocked.CompareExchange(ref _openWindow, null, window);
		(Snapshot End, long EndQpc) ended = window.Ended;
		Snapshot now = ended.End;
		long item = ended.EndQpc;
		Snapshot last = window.Baseline;
		if (now.AcquireNone - last.AcquireNone + (now.AcquireDesktopUpdate - last.AcquireDesktopUpdate) + (now.AcquireCursorOnly - last.AcquireCursorOnly) > 0 || now.Commits - last.Commits > 0)
		{
			log(Format("capture pipeline summary", displayIndex, item, in last, in now, item - window.StartQpc));
		}
	}

	public void MaybeFlushDetail(long nowQpc, int displayIndex, Action<string> log)
	{
		if (_detailEnabled)
		{
			if (!_detailArmed)
			{
				_detailArmed = true;
				_lastDetailFlushQpc = nowQpc;
				_lastDetail = Capture();
			}
			else if (nowQpc - _lastDetailFlushQpc >= DetailFlushIntervalQpc)
			{
				Snapshot now = Capture();
				log(Format("[cpu-capture-instrument]", displayIndex, nowQpc, in _lastDetail, in now, nowQpc - _lastDetailFlushQpc));
				_lastDetail = now;
				_lastDetailFlushQpc = nowQpc;
			}
		}
	}

	private string Format(string prefix, int displayIndex, long nowQpc, in Snapshot last, in Snapshot now, long windowQpc)
	{
		double num = Math.Max((double)windowQpc / (double)Stopwatch.Frequency, 0.001);
		long num2 = Interlocked.Read(in _lastDesktopUpdateQpc);
		long num3 = ((num2 == 0L) ? (-1) : TicksToMs(nowQpc - num2));
		long num4 = Delta(last.AcquireDesktopUpdate, now.AcquireDesktopUpdate);
		long num5 = Delta(last.ReadbackCount, now.ReadbackCount);
		long num6 = Delta(last.PublishCount, now.PublishCount);
		long countDelta = Delta(last.ConsumeCopyCount, now.ConsumeCopyCount);
		return string.Format(CultureInfo.InvariantCulture, "{0} display={1} window={2:F0}s acq_none={3} acq_desk={4} acq_cursor={5} accum_mean={6:F2} accum_gt1={7} accum_max={8} stale_now_ms={9} stale_ge1s={10} stale_ge10s={11} stale_ge120s={12} gate_skip={13} pre_reject={14} readback_n={15} staging_avg_ms={16:F2} map_avg_ms={17:F2} map_max_ms={18:F2} readback_copy_avg_ms={19:F2} publish_n={20} mailbox_overwrite={21} cursor_only={22} cursor_clobber={23} publish_lockwait_avg_ms={24:F2} publish_lockwait_max_ms={25:F2} consume_fresh={26} consume_pad={27} blockcopy_avg_ms={28:F2} consume_lockwait_avg_ms={29:F2} reject_window={30} reject_window_src={31} reject_commit={32} reject_commit_src={33} commits={34}", prefix, displayIndex, num, Delta(last.AcquireNone, now.AcquireNone), num4, Delta(last.AcquireCursorOnly, now.AcquireCursorOnly), MeanAccum(Delta(last.AccumulatedFramesSum, now.AccumulatedFramesSum), num4), Delta(last.AccumulatedFramesGt1, now.AccumulatedFramesGt1), now.AccumulatedFramesMax, num3, Delta(last.StaleAcquireGe1S, now.StaleAcquireGe1S), Delta(last.StaleAcquireGe10S, now.StaleAcquireGe10S), Delta(last.StaleAcquireGe120S, now.StaleAcquireGe120S), Delta(last.GateSkip, now.GateSkip), Delta(last.RouterRejectPreReadback, now.RouterRejectPreReadback), num5, AvgMs(Delta(last.StagingCopyTicks, now.StagingCopyTicks), num5), AvgMs(Delta(last.MapWaitTicks, now.MapWaitTicks), num5), TicksToMsF(now.MapWaitMaxTicks), AvgMs(Delta(last.ReadbackCopyTicks, now.ReadbackCopyTicks), num5), num6, Delta(last.MailboxOverwrite, now.MailboxOverwrite), Delta(last.CursorOnlyPublish, now.CursorOnlyPublish), Delta(last.CursorOnlyClobber, now.CursorOnlyClobber), AvgMs(Delta(last.PublishLockWaitTicks, now.PublishLockWaitTicks), num6), TicksToMsF(now.PublishLockWaitMaxTicks), Delta(last.ConsumeFresh, now.ConsumeFresh), Delta(last.ConsumePad, now.ConsumePad), AvgMs(Delta(last.ConsumeCopyTicks, now.ConsumeCopyTicks), countDelta), AvgMs(Delta(last.ConsumeLockWaitTicks, now.ConsumeLockWaitTicks), countDelta), Delta(last.RejectAfterConsumeWindow, now.RejectAfterConsumeWindow), Delta(last.RejectAfterConsumeWindowSource, now.RejectAfterConsumeWindowSource), Delta(last.RejectAfterConsumeCommit, now.RejectAfterConsumeCommit), Delta(last.RejectAfterConsumeCommitSource, now.RejectAfterConsumeCommitSource), Delta(last.Commits, now.Commits));
		static double AvgMs(long ticksDelta, long num7)
		{
			if (num7 <= 0)
			{
				return 0.0;
			}
			return (double)ticksDelta * 1000.0 / (double)Stopwatch.Frequency / (double)num7;
		}
		static long Delta(long a, long b)
		{
			return b - a;
		}
		static double MeanAccum(long sumDelta, long num7)
		{
			if (num7 <= 0)
			{
				return 0.0;
			}
			return (double)sumDelta / (double)num7;
		}
	}

	private static long TicksToMs(long ticks)
	{
		return (long)Math.Round((double)ticks * 1000.0 / (double)Stopwatch.Frequency);
	}

	private static double TicksToMsF(long ticks)
	{
		return (double)ticks * 1000.0 / (double)Stopwatch.Frequency;
	}

	private Snapshot Capture()
	{
		return new Snapshot
		{
			AcquireNone = Interlocked.Read(in _acquireNone),
			AcquireDesktopUpdate = Interlocked.Read(in _acquireDesktopUpdate),
			AcquireCursorOnly = Interlocked.Read(in _acquireCursorOnly),
			AccumulatedFramesSum = Interlocked.Read(in _accumulatedFramesSum),
			AccumulatedFramesGt1 = Interlocked.Read(in _accumulatedFramesGt1),
			AccumulatedFramesMax = Interlocked.Read(in _accumulatedFramesMax),
			StaleAcquireGe1S = Interlocked.Read(in _staleAcquireGe1S),
			StaleAcquireGe10S = Interlocked.Read(in _staleAcquireGe10S),
			StaleAcquireGe120S = Interlocked.Read(in _staleAcquireGe120S),
			RouterRejectPreReadback = Interlocked.Read(in _routerRejectPreReadback),
			GateSkip = Interlocked.Read(in _gateSkip),
			StagingCopyTicks = Interlocked.Read(in _stagingCopyTicks),
			MapWaitTicks = Interlocked.Read(in _mapWaitTicks),
			MapWaitMaxTicks = Interlocked.Read(in _mapWaitMaxTicks),
			ReadbackCopyTicks = Interlocked.Read(in _readbackCopyTicks),
			ReadbackCount = Interlocked.Read(in _readbackCount),
			PublishCount = Interlocked.Read(in _publishCount),
			PublishLockWaitTicks = Interlocked.Read(in _publishLockWaitTicks),
			PublishLockWaitMaxTicks = Interlocked.Read(in _publishLockWaitMaxTicks),
			MailboxOverwrite = Interlocked.Read(in _mailboxOverwrite),
			CursorOnlyPublish = Interlocked.Read(in _cursorOnlyPublish),
			CursorOnlyClobber = Interlocked.Read(in _cursorOnlyClobber),
			ConsumeFresh = Interlocked.Read(in _consumeFresh),
			ConsumePad = Interlocked.Read(in _consumePad),
			ConsumeLockWaitTicks = Interlocked.Read(in _consumeLockWaitTicks),
			ConsumeCopyTicks = Interlocked.Read(in _consumeCopyTicks),
			ConsumeCopyCount = Interlocked.Read(in _consumeCopyCount),
			RejectAfterConsumeWindow = Interlocked.Read(in _rejectAfterConsumeWindow),
			RejectAfterConsumeWindowSource = Interlocked.Read(in _rejectAfterConsumeWindowSource),
			RejectAfterConsumeCommit = Interlocked.Read(in _rejectAfterConsumeCommit),
			RejectAfterConsumeCommitSource = Interlocked.Read(in _rejectAfterConsumeCommitSource),
			Commits = Interlocked.Read(in _commits)
		};
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
