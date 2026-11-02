using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using KuroWindows.Input;
using KuroWindows.RecorderCore.Clock;
using KuroWindows.RecorderCore.Segments;

namespace KuroWindows.Capture;

internal sealed class RecorderWriteRouter : IEventSink, IDisposable
{
	private sealed class DropCounter
	{
		public int Value;
	}

	private readonly record struct ClosingSet(SegmentSet Set, long BlockedFromWallMs);

	private sealed class RefusalSlot
	{
		public static readonly RefusalSlot Empty = new RefusalSlot(0L, 0L, 0L);

		public readonly long Count;

		public readonly long First;

		public readonly long Last;

		private RefusalSlot(long count, long first, long last)
		{
			Count = count;
			First = first;
			Last = last;
		}

		public RefusalSlot With(long seq)
		{
			return new RefusalSlot(Count + 1, (Count == 0L || seq < First) ? seq : First, Math.Max(Last, seq));
		}
	}

	private readonly record struct GateSnapshot(SegmentSet? Active, ClosingSet[] Closing, long PrivacyBlockedFromWallMs)
	{
		public SegmentSet? Newest
		{
			get
			{
				if (Closing.Length != 0)
				{
					return Closing[^1].Set;
				}
				return null;
			}
		}
	}

	private const int BulkRefuseAt = 2048;

	private const int AllRefuseAt = 4096;

	private const int WriterIdleWaitMs = 1000;

	private const long FlushIntervalMs = 8L;

	private const long MouseMoveThinBudgetBytes = 41943040L;

	private const long EventsFileCapBytes = 52428800L;

	private const long CloseReserveBytes = 262144L;

	private const long ThinEvalIntervalMs = 1000L;

	private const long FallbackThinRowBytes = 200L;

	private readonly object _sync = new object();

	private SegmentSet? _active;

	private ClosingSet[] _closing = Array.Empty<ClosingSet>();

	private long _privacyBlockedFromWallMs = long.MaxValue;

	private bool _afterPrivacyStop;

	private int _eventWriteFailureSignaled;

	private long _gateOpenedAtQpc;

	private long _episodeCaptureFloorWallMs;

	private readonly ConcurrentDictionary<int, DropCounter> _framesDroppedSincePrev = new ConcurrentDictionary<int, DropCounter>();

	private readonly Channel<EventRow> _queue = Channel.CreateUnbounded<EventRow>(new UnboundedChannelOptions
	{
		SingleReader = true,
		SingleWriter = false
	});

	private readonly ManualResetEventSlim _wake = new ManualResetEventSlim(initialState: false);

	private readonly RowSerializer _serializer = new RowSerializer();

	private readonly RefusalSlot[] _refused = NewRefusalSlots();

	private readonly HashSet<SegmentSet> _touched = new HashSet<SegmentSet>(ReferenceEqualityComparer.Instance);

	private long _lastFlushQpc;

	private readonly List<EventRow> _preOpen = new List<EventRow>();

	private int _preOpenGen;

	private int _preOpenCounter;

	private int _preOpenListGen;

	private const int PreOpenHoldRows = 4096;

	private int _pending;

	private int _writerSleeping;

	private int _writerStarted;

	private int _completed;

	private int _writerDied;

	private Thread? _writer;

	private readonly int _mouseDeltaFlushMs = AppConfig.MouseDeltaFlushMs;

	private const long InjectedMoveSlackMs = 8L;

	private long _deltaDx;

	private long _deltaDy;

	private int _deltaPackets;

	private bool _deltaInjected;

	private long _deltaWindowStartMs;

	private long _deltaLastPacketMs;

	private long _deltaLastInjectedMoveMs = -4611686018427387904L;

	private KuroWindows.Input.AppContext? _deltaApp;

	private int _deltaScreen;

	private uint _deltaOsTime;

	private const long HousekeepingIntervalMs = 1000L;

	private const long DeliverySilenceMs = 1000L;

	private const long FallbackRearmIntervalMs = 5000L;

	private const long ButtonHeldQuietMs = 1000L;

	private long _lastHousekeepingQpc;

	private long _lastRearmPostQpc;

	private long _lastReconcilePostQpc;

	private bool _fallbackEpisodeOpen;

	private static readonly string[] BlindReasons = new string[4] { "elevated_foreground", "secure_desktop", "lock_screen", "remote_disconnect" };

	private readonly BlindInterval?[] _blind = new BlindInterval[BlindReasons.Length];

	private readonly List<(SegmentSet Set, object Payload)> _blindRows = new List<(SegmentSet, object)>();

	private const int RefusalChannels = 3;

	private const int RefusalTiers = 2;

	private static readonly string[] RefusalChannelNames = new string[3] { "mouse", "keyboard", "touchpad" };

	private static readonly string[] RefusalTierNames = new string[2] { "bulk", "all" };

	private const long InstallAcceptGraceMs = 5000L;

	public InputPlaneSource? InputPlane { get; init; }

	public RowWrittenHandler? RowWritten { get; set; }

	public SegmentSessionFactsCollector? SessionFacts { get; init; }

	public Action<Exception>? EventWriteFailed { get; set; }

	public Func<long>? SuspendFenceWallMs { get; set; }

	public int? ActiveSegmentIndex
	{
		get
		{
			lock (_sync)
			{
				return _active?.SegmentIndex;
			}
		}
	}

	public long EpisodeCaptureFloorWallMs => Interlocked.Read(in _episodeCaptureFloorWallMs);

	public event Action? GateClosed;

	private static RefusalSlot[] NewRefusalSlots()
	{
		RefusalSlot[] array = new RefusalSlot[6];
		Array.Fill(array, RefusalSlot.Empty);
		return array;
	}

	private long FenceWallMs()
	{
		return SuspendFenceWallMs?.Invoke() ?? long.MaxValue;
	}

	public bool CanAcceptFrame(long wallMs, long sourceCaptureWallMs)
	{
		long num = FenceWallMs();
		if (wallMs >= num || sourceCaptureWallMs >= num)
		{
			return false;
		}
		lock (_sync)
		{
			return _active != null && wallMs < _privacyBlockedFromWallMs && sourceCaptureWallMs >= Interlocked.Read(in _episodeCaptureFloorWallMs) && _active.CanAcceptWallMs(wallMs);
		}
	}

	public void Install(SegmentSet segment)
	{
		StartWriter();
		lock (_sync)
		{
			long num = (long)RecordingClock.Shared.NowMs();
			segment.AcceptWritesFrom(_afterPrivacyStop ? num : (num - 5000));
			_afterPrivacyStop = false;
			_active = segment;
			_privacyBlockedFromWallMs = long.MaxValue;
			Interlocked.Exchange(ref _episodeCaptureFloorWallMs, num);
			Interlocked.Exchange(ref _gateOpenedAtQpc, Stopwatch.GetTimestamp());
			EnqueueBoundary(null, segment, num);
		}
		SessionFacts?.SegmentOpened();
	}

	public long GateOpenElapsedMsOrNegative()
	{
		long num = Interlocked.Read(in _gateOpenedAtQpc);
		if (num == 0L)
		{
			return -1L;
		}
		return (Stopwatch.GetTimestamp() - num) * 1000 / Stopwatch.Frequency;
	}

	public SegmentSet? SwapForRotation(SegmentSet next, long boundaryWallMs)
	{
		SegmentSet active;
		lock (_sync)
		{
			active = _active;
			active?.BlockWritesFrom(boundaryWallMs);
			next.AcceptWritesFrom(boundaryWallMs);
			CloseAt(active, boundaryWallMs);
			_active = next;
			_privacyBlockedFromWallMs = long.MaxValue;
			CloseSessionFacts(active);
			EnqueueBoundary(active, next, boundaryWallMs);
		}
		SessionFacts?.RefreshEnvironment();
		return active;
	}

	public SegmentSet? StopForPrivacy(long blockedFromWallMs)
	{
		return StopAtBoundary(blockedFromWallMs, privacy: true);
	}

	public SegmentSet? StopForIdle(long blockedFromWallMs)
	{
		return StopAtBoundary(blockedFromWallMs, privacy: false);
	}

	public void NotePrivacyGap()
	{
		lock (_sync)
		{
			_afterPrivacyStop = true;
			Volatile.Write(ref _preOpenGen, 0);
		}
	}

	public void NoteMaskCleared(bool holdInput)
	{
		lock (_sync)
		{
			_afterPrivacyStop = false;
			Volatile.Write(ref _preOpenGen, holdInput ? (++_preOpenCounter) : 0);
		}
	}

	public void DiscardPreOpenHold()
	{
		Volatile.Write(ref _preOpenGen, 0);
	}

	private SegmentSet? StopAtBoundary(long blockedFromWallMs, bool privacy)
	{
		SegmentSet active;
		bool flag;
		lock (_sync)
		{
			_afterPrivacyStop |= privacy;
			Volatile.Write(ref _preOpenGen, (!privacy) ? (++_preOpenCounter) : 0);
			_privacyBlockedFromWallMs = blockedFromWallMs;
			active = _active;
			active?.BlockWritesFrom(blockedFromWallMs);
			CloseAt(active, blockedFromWallMs);
			_active = null;
			CloseSessionFacts(active);
			EnqueueBoundary(active, null, blockedFromWallMs);
			Interlocked.Exchange(ref _episodeCaptureFloorWallMs, 0L);
			flag = Interlocked.Exchange(ref _gateOpenedAtQpc, 0L) != 0;
		}
		if (flag)
		{
			RaiseGateClosed();
		}
		return active;
	}

	public SegmentSet? Stop()
	{
		SegmentSet active;
		bool flag;
		lock (_sync)
		{
			long boundaryWallMs = (long)RecordingClock.Shared.NowMs();
			active = _active;
			_active = null;
			Volatile.Write(ref _preOpenGen, 0);
			CloseAt(active, boundaryWallMs);
			_privacyBlockedFromWallMs = long.MinValue;
			CloseSessionFacts(active);
			EnqueueBoundary(active, null, boundaryWallMs);
			Interlocked.Exchange(ref _episodeCaptureFloorWallMs, 0L);
			flag = Interlocked.Exchange(ref _gateOpenedAtQpc, 0L) != 0;
		}
		if (flag)
		{
			RaiseGateClosed();
		}
		return active;
	}

	private void CloseSessionFacts(SegmentSet? closing)
	{
		SegmentSessionFactsCollector sessionFacts = SessionFacts;
		if (sessionFacts != null)
		{
			SegmentSessionFacts sessionFacts2 = null;
			try
			{
				sessionFacts2 = sessionFacts.SegmentClosed();
			}
			catch (Exception ex)
			{
				KuroLog.LocalOnly("capture", "session facts snapshot failed: " + ex.GetType().Name + ": " + ex.Message, "CloseSessionFacts", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\RecorderWriteRouter.cs", 566);
			}
			closing?.SetSessionFacts(sessionFacts2);
		}
	}

	private void RaiseGateClosed()
	{
		Action action = GateClosed;
		if (action == null)
		{
			return;
		}
		Delegate[] invocationList = action.GetInvocationList();
		foreach (Delegate obj in invocationList)
		{
			try
			{
				((Action)obj)();
			}
			catch (Exception ex)
			{
				KuroLog.LocalOnly("capture", "gate_close mailbox invalidation handler failed: " + ex.GetType().Name + ": " + ex.Message, "RaiseGateClosed", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\RecorderWriteRouter.cs", 594);
			}
		}
	}

	private void CloseAt(SegmentSet? old, long boundaryWallMs)
	{
		if (old != null)
		{
			ClosingSet[] closing = _closing;
			int num = 0;
			ClosingSet[] array = new ClosingSet[1 + closing.Length];
			ReadOnlySpan<ClosingSet> readOnlySpan = new ReadOnlySpan<ClosingSet>(closing);
			readOnlySpan.CopyTo(new Span<ClosingSet>(array).Slice(num, readOnlySpan.Length));
			num += readOnlySpan.Length;
			array[num] = new ClosingSet(old, boundaryWallMs);
			_closing = array;
		}
	}

	public void ClearPrevious(SegmentSet segment)
	{
		lock (_sync)
		{
			_closing = Array.FindAll(_closing, (ClosingSet closing) => closing.Set != segment);
		}
	}

	public bool CommitFrame(FrameCommitRequest frame)
	{
		SegmentSet segmentSet;
		long num;
		int num4;
		FrameCommitRequest frame2;
		while (true)
		{
			SegmentSet active;
			long privacyBlockedFromWallMs;
			long num2;
			lock (_sync)
			{
				active = _active;
				ClosingSet[] closing = _closing;
				segmentSet = ((closing.Length == 0) ? null : closing[^1].Set);
				privacyBlockedFromWallMs = _privacyBlockedFromWallMs;
				num = ((closing.Length == 0) ? long.MinValue : closing[^1].BlockedFromWallMs);
				num2 = Interlocked.Read(in _episodeCaptureFloorWallMs);
			}
			if (num2 > 0 && frame.SourceCaptureWallMs < num2)
			{
				CountDroppedFrame(frame.DisplayIndex);
				return false;
			}
			long num3 = FenceWallMs();
			if (frame.WallMs >= num3 || frame.SourceCaptureWallMs >= num3)
			{
				CountDroppedFrame(frame.DisplayIndex);
				return false;
			}
			num4 = PendingDroppedFrames(frame.DisplayIndex);
			frame2 = frame with
			{
				FramesDroppedSincePrev = num4
			};
			if (active == null || frame2.WallMs >= privacyBlockedFromWallMs)
			{
				break;
			}
			switch (active.CommitFrame(frame2))
			{
			case CommitFrameResult.Committed:
				SettleDroppedFrames(frame2.DisplayIndex, num4);
				return true;
			case CommitFrameResult.Sealed:
				CountDroppedFrame(frame2.DisplayIndex);
				return false;
			}
			lock (_sync)
			{
				if (active != _active)
				{
					continue;
				}
			}
			break;
		}
		if (segmentSet != null && frame2.WallMs < num && segmentSet.CommitFrame(frame2) == CommitFrameResult.Committed)
		{
			SettleDroppedFrames(frame2.DisplayIndex, num4);
			return true;
		}
		CountDroppedFrame(frame2.DisplayIndex);
		return false;
	}

	public void NoteFrameDropped(int displayIndex)
	{
		CountDroppedFrame(displayIndex);
	}

	private DropCounter DroppedFrames(int displayIndex)
	{
		return _framesDroppedSincePrev.GetOrAdd(displayIndex, (int _) => new DropCounter());
	}

	private void CountDroppedFrame(int displayIndex)
	{
		Interlocked.Increment(ref DroppedFrames(displayIndex).Value);
	}

	private int PendingDroppedFrames(int displayIndex)
	{
		return Volatile.Read(in DroppedFrames(displayIndex).Value);
	}

	private void SettleDroppedFrames(int displayIndex, int stamped)
	{
		if (stamped != 0)
		{
			Interlocked.Add(ref DroppedFrames(displayIndex).Value, -stamped);
		}
	}

	public void Enqueue(in EventRow row)
	{
		if (Volatile.Read(in _writerStarted) != 0 && Volatile.Read(in _completed) == 0)
		{
			int num = Volatile.Read(in _pending);
			if (num >= 4096 || (num >= 2048 && IsBulk(row.Kind)))
			{
				Refuse(in row, (num >= 4096) ? 1 : 0);
			}
			else
			{
				Post(in row);
			}
		}
	}

	public void Log(object payload, long timestampMs)
	{
		EventRow row = new EventRow
		{
			Kind = RowKind.Cold,
			Ref = payload,
			WallMs = timestampMs,
			OsTime = (uint)Environment.TickCount
		};
		Enqueue(in row);
	}

	internal bool TryEnqueueControl(in EventRow item)
	{
		if (Volatile.Read(in _writerStarted) == 0 || Volatile.Read(in _completed) != 0)
		{
			return false;
		}
		return Post(in item);
	}

	private bool Post(in EventRow row)
	{
		Interlocked.Increment(ref _pending);
		if (!_queue.Writer.TryWrite(row))
		{
			Interlocked.Decrement(ref _pending);
			return false;
		}
		if (Volatile.Read(in _writerSleeping) == 1)
		{
			_wake.Set();
		}
		return true;
	}

	private static bool IsBulk(RowKind kind)
	{
		if (kind <= RowKind.Drag || kind - 9 <= RowKind.Click)
		{
			return true;
		}
		return false;
	}

	private static int RefusalChannelOf(in EventRow row)
	{
		switch (row.Kind)
		{
		case RowKind.Key:
		case RowKind.ModifierChange:
			return 1;
		case RowKind.Touch:
			return 2;
		case RowKind.Scroll:
		case RowKind.ScrollOs:
			if (row.Channel != ScrollChannel.Wheel)
			{
				return 2;
			}
			break;
		}
		return 0;
	}

	private void Refuse(in EventRow row, int tier)
	{
		ref RefusalSlot reference = ref _refused[RefusalChannelOf(in row) * 2 + tier];
		long seq = EventSeq.Next();
		RefusalSlot refusalSlot = Volatile.Read(in reference);
		while (true)
		{
			RefusalSlot refusalSlot2 = Interlocked.CompareExchange(ref reference, refusalSlot.With(seq), refusalSlot);
			if (refusalSlot2 == refusalSlot)
			{
				break;
			}
			refusalSlot = refusalSlot2;
		}
	}

	private void EnqueueBoundary(SegmentSet? closing, SegmentSet? opening, long boundaryWallMs)
	{
		InputStateSnapshot inputStateSnapshot = null;
		if (closing != null)
		{
			InputPlaneSource inputPlane = InputPlane;
			if ((object)inputPlane != null)
			{
				inputStateSnapshot = inputPlane.HeldState();
				if (inputStateSnapshot != null)
				{
					inputPlane.SeedHeldState(inputStateSnapshot);
				}
			}
		}
		TryEnqueueControl(new EventRow
		{
			Kind = RowKind.Boundary,
			Ref = new BoundaryItem(closing, opening, boundaryWallMs, inputStateSnapshot),
			WallMs = boundaryWallMs
		});
	}

	internal BlindInterval[] OpenBlindIntervals(SegmentSet opening, long atWallMs)
	{
		lock (_sync)
		{
			InputPlaneSource inputPlane = InputPlane;
			BlindFlags flags = (((object)inputPlane != null) ? ReadBlindFlags(inputPlane, osFreshNoDelivery: false) : default(BlindFlags));
			List<BlindInterval> list = new List<BlindInterval>();
			for (int i = 0; i < _blind.Length; i++)
			{
				BlindInterval blindInterval = _blind[i];
				if (blindInterval == null)
				{
					continue;
				}
				SegmentSet reportedIn = blindInterval.ReportedIn;
				if (reportedIn != null && reportedIn != opening)
				{
					if (blindInterval.ReportedFrom > atWallMs)
					{
						continue;
					}
					long num = atWallMs - blindInterval.ReportedFrom;
					reportedIn.Counters.BlindMs += num;
					TryEnqueueControl(new EventRow
					{
						Kind = RowKind.Addressed,
						Ref = new AddressedRow(reportedIn, BlindRow(blindInterval, "end", atWallMs, num)),
						WallMs = atWallMs,
						OsTime = (uint)Environment.TickCount
					});
				}
				if (!Holds(in flags, i, blindInterval))
				{
					_blind[i] = null;
					continue;
				}
				blindInterval.ReportedIn = opening;
				blindInterval.ReportedFrom = atWallMs;
				list.Add(blindInterval);
			}
			return list.ToArray();
		}
	}

	internal static object BlindRow(BlindInterval interval, string state, long timestamp, long? durationMs = null)
	{
		return new
		{
			type = "input_blind",
			reason = interval.Reason,
			state = state,
			durationMs = durationMs,
			appName = interval.AppName,
			pid = interval.Pid,
			timestamp = timestamp
		};
	}

	private void StartWriter()
	{
		if (Interlocked.Exchange(ref _writerStarted, 1) == 0)
		{
			(_writer = new Thread(Run)
			{
				IsBackground = true,
				Name = "KuroEvents"
			}).Start();
		}
	}

	public void Complete(TimeSpan join)
	{
		if (Interlocked.Exchange(ref _completed, 1) == 0)
		{
			_queue.Writer.TryComplete();
			_wake.Set();
		}
		Thread writer = _writer;
		if (writer != null && writer != Thread.CurrentThread)
		{
			writer.Join(join);
		}
	}

	public void Dispose()
	{
		Complete(TimeSpan.FromSeconds(2.0));
	}

	private void Run()
	{
		ChannelReader<EventRow> reader = _queue.Reader;
		do
		{
			int num = HeldFlushMs();
			if (num > 0)
			{
				_wake.Wait(num);
				_wake.Reset();
			}
			else if (_touched.Count == 0 && Volatile.Read(in _pending) == 0 && !reader.Completion.IsCompleted)
			{
				Interlocked.Exchange(ref _writerSleeping, 1);
				if (Volatile.Read(in _pending) == 0 && !reader.Completion.IsCompleted)
				{
					_wake.Wait((_deltaPackets > 0) ? _mouseDeltaFlushMs : 1000);
				}
				_wake.Reset();
				Volatile.Write(ref _writerSleeping, 0);
			}
		}
		while (!DrainPass(reader));
	}

	private int HeldFlushMs()
	{
		if (_touched.Count == 0)
		{
			return 0;
		}
		long num = RecordingClock.MsBetween(_lastFlushQpc, Stopwatch.GetTimestamp());
		if (num < 8)
		{
			return (int)(8 - num);
		}
		return 0;
	}

	private bool DrainPass(ChannelReader<EventRow> reader)
	{
		GateSnapshot snapshot = Snapshot();
		if (_preOpen.Count > 0 && Volatile.Read(in _writerDied) == 0)
		{
			ReplayPreOpen(ref snapshot, _touched);
		}
		int num = Volatile.Read(in _pending);
		if (snapshot.Active != null && num > snapshot.Active.Counters.QueuePeak)
		{
			snapshot.Active.Counters.QueuePeak = num;
		}
		HashSet<SegmentSet> touched = _touched;
		EventRow item;
		while (reader.TryRead(out item))
		{
			Interlocked.Decrement(ref _pending);
			try
			{
				if (Volatile.Read(in _writerDied) != 0)
				{
					DropDead(in item, snapshot, touched);
				}
				else
				{
					Route(in item, ref snapshot, touched);
				}
			}
			catch (Exception ex)
			{
				NoteWriterFault(ex);
				DropDead(in item, snapshot, touched);
			}
		}
		bool isCompleted = reader.Completion.IsCompleted;
		long timestamp = Stopwatch.GetTimestamp();
		if (Volatile.Read(in _writerDied) == 0)
		{
			try
			{
				long num2 = (long)RecordingClock.Shared.NowMs();
				if (_deltaPackets > 0 && (isCompleted || num2 - _deltaLastPacketMs >= _mouseDeltaFlushMs))
				{
					WriteMouseDelta(ref snapshot, touched);
				}
				SegmentSet active = snapshot.Active;
				if (active != null && active.AcceptsEvent(num2) && Volatile.Read(in _pending) < 2048)
				{
					WriteOverflowRows(active, num2, touched);
				}
				CheckSizeBudget(snapshot.Active, num2, touched);
				if (InputPlane != null && !isCompleted && RecordingClock.MsBetween(_lastHousekeepingQpc, timestamp) >= 1000)
				{
					_lastHousekeepingQpc = timestamp;
					Housekeeping(timestamp, ref snapshot, touched);
				}
			}
			catch (Exception ex2)
			{
				NoteWriterFault(ex2);
			}
		}
		if (touched.Count > 0 && (isCompleted || RecordingClock.MsBetween(_lastFlushQpc, timestamp) >= 8))
		{
			foreach (SegmentSet item2 in touched)
			{
				try
				{
					item2.Files.Flush();
				}
				catch (Exception ex3)
				{
					NoteWriterFault(ex3);
				}
			}
			touched.Clear();
			_lastFlushQpc = timestamp;
		}
		return isCompleted;
	}

	private GateSnapshot Snapshot()
	{
		lock (_sync)
		{
			return new GateSnapshot(_active, _closing, _privacyBlockedFromWallMs);
		}
	}

	private void Route(in EventRow item, ref GateSnapshot snapshot, HashSet<SegmentSet> touched)
	{
		switch (item.Kind)
		{
		case RowKind.Cold:
			RouteByTime(in item, ref snapshot, touched);
			break;
		case RowKind.Addressed:
		{
			AddressedRow addressedRow = (AddressedRow)item.Ref;
			AppendCold(addressedRow.Set, addressedRow.Payload, item.OsTime, touched);
			break;
		}
		case RowKind.FrameZero:
		{
			SegmentSet segmentSet = (SegmentSet)item.Ref;
			long seq = EventSeq.Next();
			if (segmentSet.Files.Append(item.Display, _serializer.FrameZero(item.WallMs, item.Display, seq, item.OsTime), seq))
			{
				touched.Add(segmentSet);
			}
			else
			{
				segmentSet.Counters.Dropped++;
			}
			break;
		}
		case RowKind.SegmentEnd:
		{
			SegmentEndItem segmentEndItem = (SegmentEndItem)item.Ref;
			try
			{
				if (!segmentEndItem.Set.Files.Closed)
				{
					segmentEndItem.Set.WriteSegmentEndRows(_serializer, segmentEndItem.Timestamps, segmentEndItem.Reason, segmentEndItem.MaskTrigger, segmentEndItem.MaskDetail);
				}
				break;
			}
			finally
			{
				CloseFiles(segmentEndItem.Set, segmentEndItem.Barrier, touched);
			}
		}
		case RowKind.Barrier:
		{
			SealBarrier sealBarrier = (SealBarrier)item.Ref;
			CloseFiles(sealBarrier.Set, sealBarrier, touched);
			break;
		}
		case RowKind.Boundary:
		{
			BoundaryItem boundaryItem = (BoundaryItem)item.Ref;
			SegmentSet closing = boundaryItem.Closing;
			if (closing != null)
			{
				if (_deltaPackets > 0)
				{
					WriteMouseDelta(ref snapshot, touched);
				}
				WriteOverflowRows(closing, boundaryItem.BoundaryWallMs, touched);
				if (boundaryItem.Held != null)
				{
					AppendCold(closing, boundaryItem.Held.ToRow("segment_close", boundaryItem.BoundaryWallMs), (uint)Environment.TickCount, touched);
				}
				HandOverBlindIntervals(closing, boundaryItem.Opening, boundaryItem.BoundaryWallMs, touched);
				closing.Files.Flush();
			}
			if (boundaryItem.Opening == null)
			{
				break;
			}
			ChannelCapabilities? channelCapabilities = InputPlane?.Channels();
			if (channelCapabilities.HasValue)
			{
				ChannelCapabilities valueOrDefault = channelCapabilities.GetValueOrDefault();
				if (valueOrDefault.RawMouse && valueOrDefault.RawKeyboard)
				{
					boundaryItem.Opening.Counters.Witness = true;
				}
			}
			break;
		}
		case RowKind.RawDelta:
			NoteRawDelta(in item, ref snapshot, touched);
			break;
		case RowKind.InjectedMove:
			NoteInjectedMove(item.WallMs);
			break;
		case RowKind.MouseMove:
		case RowKind.Drag:
			if ((item.Flags & RowFlags.Injected) != 0)
			{
				NoteInjectedMove(item.WallMs);
			}
			RouteByTime(in item, ref snapshot, touched);
			break;
		default:
			RouteByTime(in item, ref snapshot, touched);
			break;
		}
	}

	private void NoteInjectedMove(long wallMs)
	{
		_deltaLastInjectedMoveMs = wallMs;
		if (_deltaPackets > 0 && wallMs - _deltaLastPacketMs <= 8)
		{
			_deltaInjected = true;
		}
	}

	private void NoteRawDelta(in EventRow packet, ref GateSnapshot snapshot, HashSet<SegmentSet> touched)
	{
		if (_deltaPackets > 0 && packet.WallMs - _deltaWindowStartMs >= _mouseDeltaFlushMs)
		{
			WriteMouseDelta(ref snapshot, touched);
		}
		if (_deltaPackets == 0)
		{
			_deltaWindowStartMs = packet.WallMs;
		}
		_deltaDx += packet.Dx;
		_deltaDy += packet.Dy;
		_deltaPackets++;
		if (packet.WallMs - _deltaLastInjectedMoveMs <= 8)
		{
			_deltaInjected = true;
		}
		_deltaLastPacketMs = packet.WallMs;
		_deltaApp = packet.App;
		_deltaScreen = packet.Screen;
		_deltaOsTime = packet.OsTime;
	}

	private void WriteMouseDelta(ref GateSnapshot snapshot, HashSet<SegmentSet> touched)
	{
		EventRow item = new EventRow
		{
			Kind = RowKind.MouseDelta,
			WallMs = _deltaLastPacketMs,
			OsTime = _deltaOsTime,
			SourcePid = (_deltaInjected ? (-1) : 0),
			App = _deltaApp,
			Screen = _deltaScreen,
			Dx = (int)_deltaDx,
			Dy = (int)_deltaDy,
			Packets = _deltaPackets
		};
		_deltaDx = 0L;
		_deltaDy = 0L;
		_deltaPackets = 0;
		_deltaInjected = false;
		RouteByTime(in item, ref snapshot, touched);
	}

	private void RouteByTime(in EventRow item, ref GateSnapshot snapshot, HashSet<SegmentSet> touched)
	{
		long wallMs = item.WallMs;
		if (wallMs > FenceWallMs())
		{
			return;
		}
		while (true)
		{
			SegmentSet active = snapshot.Active;
			if (active == null || wallMs >= snapshot.PrivacyBlockedFromWallMs)
			{
				break;
			}
			if (active.AcceptsEvent(wallMs))
			{
				Append(active, in item, touched);
				return;
			}
			GateSnapshot gateSnapshot = Snapshot();
			if (active == gateSnapshot.Active)
			{
				break;
			}
			snapshot = gateSnapshot;
		}
		SegmentSet segmentSet = null;
		ClosingSet[] closing = snapshot.Closing;
		for (int num = closing.Length - 1; num >= 0; num--)
		{
			var (segmentSet3, num3) = closing[num];
			if (wallMs <= num3)
			{
				if (segmentSet3.AcceptsEvent(wallMs))
				{
					Append(segmentSet3, in item, touched);
					return;
				}
				if (segmentSet == null)
				{
					segmentSet = segmentSet3;
				}
			}
		}
		if (segmentSet != null)
		{
			segmentSet.Counters.Late++;
		}
		else
		{
			if (snapshot.Active != null)
			{
				return;
			}
			int num4 = Volatile.Read(in _preOpenGen);
			if (num4 != 0)
			{
				if (num4 != _preOpenListGen)
				{
					_preOpen.Clear();
					_preOpenListGen = num4;
				}
				if (_preOpen.Count >= 4096)
				{
					_preOpen.RemoveRange(0, 2048);
				}
				_preOpen.Add(item);
			}
		}
	}

	private void ReplayPreOpen(ref GateSnapshot snapshot, HashSet<SegmentSet> touched)
	{
		int num = Volatile.Read(in _preOpenGen);
		if (num == 0 || num != _preOpenListGen)
		{
			_preOpen.Clear();
		}
		else
		{
			if (snapshot.Active == null)
			{
				return;
			}
			EventRow[] array = _preOpen.ToArray();
			_preOpen.Clear();
			EventRow[] array2 = array;
			for (int i = 0; i < array2.Length; i++)
			{
				EventRow item = array2[i];
				try
				{
					RouteByTime(in item, ref snapshot, touched);
				}
				catch (Exception ex)
				{
					NoteWriterFault(ex);
					DropDead(in item, snapshot, touched);
				}
			}
		}
	}

	private void Append(SegmentSet set, in EventRow item, HashSet<SegmentSet> touched)
	{
		SegmentWriteCounters counters = set.Counters;
		RowKind kind = item.Kind;
		bool flag = kind <= RowKind.Drag;
		bool flag2 = flag;
		if (flag2 && counters.ThrottleMs > 0)
		{
			if (item.WallMs - counters.LastThinWallMs < counters.ThrottleMs)
			{
				counters.Thinned++;
				return;
			}
			counters.LastThinWallMs = item.WallMs;
		}
		long seq = EventSeq.Next();
		ReadOnlySpan<byte> line = ((item.Kind == RowKind.Cold) ? _serializer.SerializeCold(item.Ref, seq, item.OsTime) : _serializer.Serialize(in item, seq));
		if (!set.Files.AppendAll(line, seq))
		{
			counters.Dropped++;
			return;
		}
		if (flag2)
		{
			counters.ThinRows++;
			counters.ThinRowBytes += line.Length + 2;
		}
		touched.Add(set);
		RowWritten?.Invoke(line, seq);
	}

	private void CheckSizeBudget(SegmentSet? active, long nowMs, HashSet<SegmentSet> touched)
	{
		if (active == null || active.Files.Closed || !active.AcceptsEvent(nowMs))
		{
			return;
		}
		SegmentWriteCounters counters = active.Counters;
		if (counters.ThrottleMs == long.MaxValue || (counters.ThrottleMs > 0 && nowMs - counters.ThrottleEvalMs < 1000))
		{
			return;
		}
		SegmentEventFiles files = active.Files;
		IReadOnlyList<SegmentDisplayManifest> displays = active.Displays;
		long num = 0L;
		for (int i = 0; i < displays.Count; i++)
		{
			num = Math.Max(num, files.BytesWritten(displays[i].DisplayIndex));
		}
		if (num < 41943040)
		{
			return;
		}
		counters.ThrottleEvalMs = nowMs;
		long num2 = Math.Max(1L, nowMs - active.StartedWallMs);
		long num3 = Math.Max(1000L, active.StartedWallMs + (long)AppConfig.ClipDurationSeconds * 1000L - nowMs);
		long num4 = num - counters.ThinRowBytes;
		long num5 = 52166656 - num - num4 * num3 / num2;
		long num6 = ((counters.ThinRows > 0) ? (counters.ThinRowBytes / counters.ThinRows) : 200);
		long num7 = ((num5 <= num6) ? long.MaxValue : Math.Max(AppConfig.MouseThrottleMs, (num3 * num6 + num5 - 1) / num5));
		if (num7 > counters.ThrottleMs)
		{
			counters.ThrottleMs = num7;
			long num8 = EventSeq.Next();
			object payload = ((num7 == long.MaxValue) ? ((object)new
			{
				type = "input_overflow",
				channel = "mouse",
				tier = "size_cap",
				dropped = counters.Thinned,
				seqFrom = num8,
				seqTo = num8,
				timestamp = nowMs
			}) : ((object)new
			{
				type = "input_overflow",
				channel = "mouse",
				tier = "size_budget",
				dropped = counters.Thinned,
				seqFrom = num8,
				seqTo = num8,
				throttleMs = num7,
				timestamp = nowMs
			}));
			if (files.AppendAll(_serializer.SerializeCold(payload, num8, (uint)Environment.TickCount), num8))
			{
				touched.Add(active);
			}
			else
			{
				counters.Dropped++;
			}
		}
	}

	private static void CloseFiles(SegmentSet set, SealBarrier barrier, HashSet<SegmentSet> touched)
	{
		try
		{
			set.Files.Close();
		}
		catch (Exception error)
		{
			barrier.Error = error;
		}
		finally
		{
			touched.Remove(set);
			barrier.Done.Set();
		}
	}

	private static void DropDead(in EventRow item, GateSnapshot snapshot, HashSet<SegmentSet> touched)
	{
		switch (item.Kind)
		{
		case RowKind.SegmentEnd:
		{
			SegmentEndItem segmentEndItem = (SegmentEndItem)item.Ref;
			CloseFiles(segmentEndItem.Set, segmentEndItem.Barrier, touched);
			return;
		}
		case RowKind.Barrier:
		{
			SealBarrier sealBarrier = (SealBarrier)item.Ref;
			CloseFiles(sealBarrier.Set, sealBarrier, touched);
			return;
		}
		case RowKind.Boundary:
			return;
		}
		SegmentSet segmentSet = snapshot.Active ?? snapshot.Newest;
		if (segmentSet != null)
		{
			segmentSet.Counters.Dropped++;
		}
	}

	private void WriteOverflowRows(SegmentSet set, long timestamp, HashSet<SegmentSet> touched)
	{
		for (int i = 0; i < _refused.Length; i++)
		{
			if (Volatile.Read(in _refused[i]).Count != 0L)
			{
				RefusalSlot refusalSlot = Interlocked.Exchange(ref _refused[i], RefusalSlot.Empty);
				var payload = new
				{
					type = "input_overflow",
					channel = RefusalChannelNames[i / 2],
					tier = RefusalTierNames[i % 2],
					dropped = refusalSlot.Count,
					seqFrom = refusalSlot.First,
					seqTo = refusalSlot.Last,
					timestamp = timestamp
				};
				AppendCold(set, payload, (uint)Environment.TickCount, touched);
				set.Counters.Dropped += refusalSlot.Count;
			}
		}
	}

	private void Housekeeping(long nowQpc, ref GateSnapshot snapshot, HashSet<SegmentSet> touched)
	{
		InputPlaneSource inputPlane = InputPlane;
		long num = RecordingClock.Shared.WallMsFromQpc(nowQpc);
		long num2 = SystemAvailabilityEvents.LastInputAgeMs();
		long num3 = inputPlane.LastDeliveryQpc();
		long num4 = ((num3 == 0L) ? 0 : RecordingClock.MsBetween(num3, nowQpc));
		bool flag = num3 != 0L && num2 >= 0 && num2 < 1000 && num4 >= 1000;
		BlindFlags flags = ReadBlindFlags(inputPlane, flag);
		UpdateBlindIntervals(in flags, num, touched);
		bool flag2 = flags.ForegroundElevated || flags.SessionNotInteractive || flags.SecureDesktop;
		if (flag && !flag2)
		{
			if (!_fallbackEpisodeOpen)
			{
				_fallbackEpisodeOpen = true;
				EventRow item = new EventRow
				{
					Kind = RowKind.Cold,
					Ref = new
					{
						type = "input_gap",
						channel = "mouse",
						fromTimestamp = RecordingClock.Shared.WallMsFromQpc(num3),
						gapMs = num4,
						witness = "last_input_info",
						action = "rearm_posted",
						timestamp = num
					},
					WallMs = num,
					OsTime = (uint)Environment.TickCount
				};
				RouteByTime(in item, ref snapshot, touched);
				KuroLog.LocalOnly("system", $"input hook rearm posted by housekeeping os_input_age_ms={num2} silence_ms={num4}", "Housekeeping", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\RecorderWriteRouter.cs", 1653);
			}
			if (RecordingClock.MsBetween(_lastRearmPostQpc, nowQpc) >= 5000)
			{
				_lastRearmPostQpc = nowQpc;
				inputPlane.PostRearm();
			}
		}
		else if (num4 < 1000 && num3 > inputPlane.LastRearmQpc())
		{
			_fallbackEpisodeOpen = false;
		}
		long num5 = inputPlane.ButtonHeldSinceQpc();
		if (num5 != 0L && RecordingClock.MsBetween(num5, nowQpc) >= 1000 && RecordingClock.MsBetween(_lastReconcilePostQpc, nowQpc) >= 1000)
		{
			_lastReconcilePostQpc = nowQpc;
			inputPlane.PostReconcileButtons();
		}
	}

	private static BlindFlags ReadBlindFlags(InputPlaneSource input, bool osFreshNoDelivery)
	{
		BlindFlags blindFlags = input.BlindFlags();
		if (blindFlags.DesktopSwitched | osFreshNoDelivery)
		{
			blindFlags = blindFlags with
			{
				SecureDesktop = input.SecureDesktopOwnsInput()
			};
		}
		return blindFlags;
	}

	private static bool Holds(in BlindFlags flags, int i, BlindInterval? open)
	{
		return i switch
		{
			0 => flags.ForegroundElevated && (open == null || open.Pid == flags.ForegroundPid), 
			1 => flags.SecureDesktop, 
			2 => flags.SessionNotInteractive && !flags.SessionDisconnected, 
			_ => flags.SessionDisconnected, 
		};
	}

	private void UpdateBlindIntervals(in BlindFlags flags, long now, HashSet<SegmentSet> touched)
	{
		lock (_sync)
		{
			SegmentSet active = _active;
			for (int i = 0; i < BlindReasons.Length; i++)
			{
				BlindInterval blindInterval = _blind[i];
				if (blindInterval != null && !Holds(in flags, i, blindInterval))
				{
					CloseBlindInterval(blindInterval, now);
					blindInterval = (_blind[i] = null);
				}
				if (blindInterval == null && Holds(in flags, i, null))
				{
					blindInterval = new BlindInterval(BlindReasons[i], flags.ForegroundApp, flags.ForegroundPid)
					{
						ReportedFrom = now
					};
					if (active != null && !active.Files.Closed)
					{
						blindInterval.ReportedIn = active;
						_blindRows.Add((active, BlindRow(blindInterval, "begin", now)));
					}
					_blind[i] = blindInterval;
				}
			}
		}
		AppendBlindRows(touched);
	}

	private long CloseBlindInterval(BlindInterval interval, long now)
	{
		long num = Math.Max(now, interval.ReportedFrom);
		SegmentSet reportedIn = interval.ReportedIn;
		if (reportedIn == null)
		{
			return num;
		}
		long num2 = num - interval.ReportedFrom;
		reportedIn.Counters.BlindMs += num2;
		if (!reportedIn.Files.Closed)
		{
			_blindRows.Add((reportedIn, BlindRow(interval, "end", num, num2)));
		}
		return num;
	}

	private void HandOverBlindIntervals(SegmentSet closing, SegmentSet? opening, long boundaryWallMs, HashSet<SegmentSet> touched)
	{
		lock (_sync)
		{
			BlindInterval[] blind = _blind;
			foreach (BlindInterval blindInterval in blind)
			{
				if (blindInterval != null && blindInterval.ReportedIn == closing)
				{
					long timestamp = (blindInterval.ReportedFrom = CloseBlindInterval(blindInterval, boundaryWallMs));
					if (opening != null && !opening.Files.Closed)
					{
						blindInterval.ReportedIn = opening;
						_blindRows.Add((opening, BlindRow(blindInterval, "begin", timestamp)));
					}
					else
					{
						blindInterval.ReportedIn = null;
					}
				}
			}
		}
		AppendBlindRows(touched);
	}

	private void AppendBlindRows(HashSet<SegmentSet> touched)
	{
		foreach (var (set, payload) in _blindRows)
		{
			AppendCold(set, payload, (uint)Environment.TickCount, touched);
		}
		_blindRows.Clear();
	}

	private void AppendCold(SegmentSet set, object payload, uint osTime, HashSet<SegmentSet> touched)
	{
		long seq = EventSeq.Next();
		if (set.Files.AppendAll(_serializer.SerializeCold(payload, seq, osTime), seq))
		{
			touched.Add(set);
		}
		else
		{
			set.Counters.Dropped++;
		}
	}

	private void NoteWriterFault(Exception ex)
	{
		if (ex is IOException)
		{
			NoteEventWriteFailure(ex);
		}
		else if (Interlocked.Exchange(ref _writerDied, 1) == 0)
		{
			KuroLog.Capture("events writer died: " + ex.GetType().Name + ": " + ex.Message, "NoteWriterFault", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\RecorderWriteRouter.cs", 1829);
			EventWriteFailed?.Invoke(ex);
		}
	}

	private void NoteEventWriteFailure(Exception ex)
	{
		if (Interlocked.Exchange(ref _eventWriteFailureSignaled, 1) == 0)
		{
			KuroLog.Capture("events write failed; escalating capture failure: " + ex.GetType().Name + ": " + ex.Message, "NoteEventWriteFailure", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\RecorderWriteRouter.cs", 1844);
			EventWriteFailed?.Invoke(ex);
		}
	}

	void IEventSink.Enqueue(in EventRow row)
	{
		Enqueue(in row);
	}
}
