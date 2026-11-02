using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace SystemAudio;

public sealed class SystemAudioSession : ISystemAudio, IDisposable
{
	private enum CommandKind
	{
		Open,
		End,
		Seal
	}

	private readonly record struct Command(CommandKind Kind, int SegmentIndex);

	private sealed record EndInstant(long WallMs, long Qpc);

	private sealed class SegmentState(int index)
	{
		public readonly int Index = index;

		public int OpenClaimed;

		public int OpenPublished;

		public int SealPosted;

		public long AnchorWallMs;

		public long AnchorQpc;

		public volatile bool Off;

		public volatile SystemAudioSealHandle? Handle;

		public volatile AudioSegmentWriter? Writer;

		public volatile SystemAudioFacts? FailedFacts;

		private volatile EndInstant? _end;

		public EndInstant? End => _end;

		public void PostEnd(long wallMs, long qpc)
		{
			EndInstant value = new EndInstant(wallMs, qpc);
			EndInstant end;
			do
			{
				end = _end;
			}
			while (((object)end == null || end.WallMs > wallMs) && !(Interlocked.CompareExchange(ref _end, value, end) == end));
		}

		public void Fail(string reason, SystemAudioMixFormat? mixFormat = null)
		{
			FailedFacts = new SystemAudioFacts
			{
				State = "failed",
				Reason = reason,
				AnchorWallMs = AnchorWallMs,
				EndWallMs = _end?.WallMs,
				MixFormat = mixFormat
			};
		}

		public SystemAudioFacts? TimedOutFacts()
		{
			AudioSegmentWriter writer = Writer;
			if (writer != null)
			{
				return writer.TimedOutSnapshot();
			}
			SystemAudioFacts failedFacts = FailedFacts;
			if ((object)failedFacts != null)
			{
				SystemAudioFacts systemAudioFacts = failedFacts with { };
				EndInstant? end = _end;
				systemAudioFacts.EndWallMs = (((object)end != null) ? new long?(end.WallMs) : failedFacts.EndWallMs);
				return systemAudioFacts;
			}
			if (Off)
			{
				return null;
			}
			return new SystemAudioFacts
			{
				State = "failed",
				Reason = "finalize_timeout",
				AnchorWallMs = AnchorWallMs,
				EndWallMs = _end?.WallMs
			};
		}
	}

	private const int SealRingWaitMs = 300;

	private const int FirstPacketGraceMs = 1000;

	private const int FeederCadenceMs = 50;

	private const int ConsumerSeedMs = 100;

	private const int ConsumerSeedFrames = 4800;

	private const int FeederNone = 0;

	private const int FeederQueued = 1;

	private const int FeederStarted = 2;

	private static readonly TimeSpan DisposeSealFloor = TimeSpan.FromSeconds(2.0);

	private static readonly TimeSpan FeederJoinBound = TimeSpan.FromSeconds(25.0);

	private static readonly TimeSpan CaptureJoinBound = TimeSpan.FromSeconds(3.0);

	private static readonly TimeSpan DeadCaptureJoinBound = TimeSpan.FromMilliseconds(100.0);

	private readonly string _sessionDir;

	private readonly AudioClock _clock;

	private readonly SampleRing _ring;

	private readonly CaptureCounters _captureCounters = new CaptureCounters();

	private readonly ConcurrentQueue<Command> _commands = new ConcurrentQueue<Command>();

	private readonly AutoResetEvent _wake = new AutoResetEvent(initialState: false);

	private readonly ConcurrentDictionary<int, SegmentState> _segments = new ConcurrentDictionary<int, SegmentState>();

	private readonly List<SegmentState> _openWriters = new List<SegmentState>();

	private readonly Func<(SystemAudioMixFormat? MixFormat, bool Keepalive)> _streamFacts;

	private Thread? _feeder;

	private int _feederState;

	private volatile bool _feederExited;

	private AudioEndpoint? _endpoint;

	private LoopbackCapture? _capture;

	private bool _captureDeathHandled;

	private bool _reopenOnNextOpen;

	private long _firstWriterAt = -1L;

	private volatile bool _disposing;

	private int _disposed;

	public string PolicyDescription
	{
		get
		{
			try
			{
				return SystemAudioPolicy.Describe();
			}
			catch
			{
				return "off:default";
			}
		}
	}

	public SystemAudioSession(string sessionDir)
	{
		_sessionDir = sessionDir;
		_clock = new AudioClock(Stopwatch.GetTimestamp());
		_ring = new SampleRing(_clock);
		_streamFacts = () =>
		{
			LoopbackCapture capture = _capture;
			return (MixFormat: capture?.MixFormat, Keepalive: capture?.Keepalive ?? false);
		};
	}

	public void PostOpen(int segmentIndex, long anchorWallMs, long anchorQpc)
	{
		if (_disposing || !SystemAudioRuntime.IsConfigured)
		{
			return;
		}
		SegmentState orAdd = _segments.GetOrAdd(segmentIndex, (int index) => new SegmentState(index));
		if (Interlocked.CompareExchange(ref orAdd.OpenClaimed, 1, 0) != 0)
		{
			return;
		}
		orAdd.AnchorWallMs = anchorWallMs;
		orAdd.AnchorQpc = anchorQpc;
		orAdd.Handle = new SystemAudioSealHandle(orAdd.TimedOutFacts);
		Volatile.Write(ref orAdd.OpenPublished, 1);
		if (!SystemAudioPolicy.PreviewEnabled())
		{
			orAdd.Off = true;
			return;
		}
		_commands.Enqueue(new Command(CommandKind.Open, segmentIndex));
		if (Interlocked.CompareExchange(ref _feederState, 1, 0) == 0)
		{
			ThreadPool.UnsafeQueueUserWorkItem((SystemAudioSession session) =>
			{
				session.StartFeeder();
			}, this, preferLocal: false);
		}
		Wake();
	}

	public void PostEnd(int segmentIndex, long endWallMs, long endQpc)
	{
		SegmentState orAdd = _segments.GetOrAdd(segmentIndex, (int index) => new SegmentState(index));
		orAdd.PostEnd(endWallMs, endQpc);
		if (!orAdd.Off && Volatile.Read(in _feederState) != 0 && !_feederExited)
		{
			_commands.Enqueue(new Command(CommandKind.End, segmentIndex));
			Wake();
		}
	}

	public SystemAudioSealHandle PostSeal(int segmentIndex, long endWallMs, long endQpc)
	{
		SegmentState orAdd = _segments.GetOrAdd(segmentIndex, (int index) => new SegmentState(index));
		if (Volatile.Read(in orAdd.OpenPublished) == 0)
		{
			return SystemAudioSealHandle.None;
		}
		orAdd.PostEnd(endWallMs, endQpc);
		SystemAudioSealHandle handle = orAdd.Handle;
		if (Interlocked.Exchange(ref orAdd.SealPosted, 1) != 0)
		{
			return handle;
		}
		if (orAdd.Off || Volatile.Read(in _feederState) == 0)
		{
			handle.Complete(null, null);
			return handle;
		}
		if (_feederExited)
		{
			CompleteWithoutFeeder(orAdd);
			return handle;
		}
		_commands.Enqueue(new Command(CommandKind.Seal, segmentIndex));
		Wake();
		return handle;
	}

	public SystemAudioFacts? LiveFacts(int segmentIndex)
	{
		if (!_segments.TryGetValue(segmentIndex, out SegmentState value))
		{
			return null;
		}
		AudioSegmentWriter writer = value.Writer;
		SystemAudioSealHandle handle = value.Handle;
		if (writer == null || handle == null || handle.IsCompleted)
		{
			return null;
		}
		try
		{
			return writer.LiveSnapshot();
		}
		catch
		{
			return null;
		}
	}

	public void Dispose()
	{
		if (Interlocked.Exchange(ref _disposed, 1) != 0)
		{
			return;
		}
		try
		{
			_disposing = true;
			long timestamp = Stopwatch.GetTimestamp();
			List<SystemAudioSealHandle> list = new List<SystemAudioSealHandle>();
			foreach (SegmentState value in _segments.Values)
			{
				if (Volatile.Read(in value.OpenPublished) == 1 && Volatile.Read(in value.SealPosted) == 0)
				{
					long endWallMs = value.AnchorWallMs + (timestamp - value.AnchorQpc) * 1000 / Stopwatch.Frequency;
					list.Add(PostSeal(value.Index, endWallMs, timestamp));
				}
			}
			long num = Stopwatch.GetTimestamp() + DisposeSealFloor.Ticks * Stopwatch.Frequency / 10000000;
			foreach (SystemAudioSealHandle item in list)
			{
				long num2 = (num - Stopwatch.GetTimestamp()) * 1000 / Stopwatch.Frequency;
				if (num2 <= 0 || !item.Wait(TimeSpan.FromMilliseconds(num2)))
				{
					break;
				}
			}
			Wake();
			SpinWait spinWait = default;
			while (Volatile.Read(in _feederState) == 1)
			{
				spinWait.SpinOnce();
			}
			bool num3 = _feeder?.Join(FeederJoinBound) ?? true;
			LoopbackCapture capture = _capture;
			bool flag = true;
			if (capture != null)
			{
				capture.Stop();
				flag = capture.Join(CaptureJoinBound);
			}
			if (flag)
			{
				_endpoint?.Dispose();
			}
			if (num3)
			{
				_wake.Dispose();
			}
		}
		catch
		{
		}
	}

	private void Wake()
	{
		try
		{
			_wake.Set();
		}
		catch (ObjectDisposedException)
		{
		}
	}

	private void StartFeeder()
	{
		try
		{
			Thread thread = new Thread(FeederLoop, 524288)
			{
				IsBackground = true,
				Name = Settings.ThreadNamePrefix + "AudioWrite"
			};
			thread.Start();
			_feeder = thread;
		}
		catch (Exception ex)
		{
			FeederFailed(ex);
		}
		finally
		{
			Volatile.Write(ref _feederState, 2);
		}
	}

	private void FeederLoop()
	{
		try
		{
			do
			{
				bool flag = _openWriters.Count == 0 && _capture == null && !_disposing;
				_wake.WaitOne(flag ? (-1) : 50);
				ServiceCapture();
				DrainCommands();
				FeedOpenWriters();
			}
			while (!_disposing || !_commands.IsEmpty || _openWriters.Count != 0);
		}
		catch (Exception ex)
		{
			FeederFailed(ex);
		}
		finally
		{
			_feederExited = true;
		}
	}

	private void FeederFailed(Exception ex)
	{
		Log.Capture("system audio feeder failed: " + ex.GetType().Name + ": " + ex.Message, "FeederFailed", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\shared\\dotnet\\SystemAudio\\SystemAudioSession.cs", 316);
		_feederExited = true;
		foreach (SegmentState value in _segments.Values)
		{
			CompleteWithoutFeeder(value);
		}
	}

	private static void CompleteWithoutFeeder(SegmentState state)
	{
		SystemAudioSealHandle handle = state.Handle;
		if (handle != null && !handle.IsCompleted)
		{
			SystemAudioFacts systemAudioFacts = state.TimedOutFacts();
			if ((object)systemAudioFacts != null)
			{
				handle.RecordOutcome(succeeded: false, systemAudioFacts.Reason);
			}
			handle.Complete(systemAudioFacts, null);
		}
	}

	private void DrainCommands()
	{
		Command result;
		while (_commands.TryDequeue(out result))
		{
			if (!_segments.TryGetValue(result.SegmentIndex, out SegmentState value))
			{
				continue;
			}
			switch (result.Kind)
			{
			case CommandKind.Open:
				ProcessOpen(value);
				break;
			case CommandKind.End:
			{
				EndInstant end = value.End;
				if ((object)end != null)
				{
					value.Writer?.SetEnd(_clock.IndexFromQpc(end.Qpc), end.WallMs);
				}
				break;
			}
			default:
				ProcessSeal(value);
				break;
			}
		}
	}

	private void ProcessOpen(SegmentState state)
	{
		(bool, string) tuple = SystemAudioPolicy.SnapshotForOpen();
		if (!tuple.Item1)
		{
			if (tuple.Item2 == "latch")
			{
				state.Fail("latched_off");
			}
			else
			{
				state.Off = true;
			}
			return;
		}
		try
		{
			if (_openWriters.Count == 0)
			{
				_ring.AdvanceConsumed(_clock.IndexFromQpc(state.AnchorQpc) - 4800);
			}
			LoopbackCapture loopbackCapture = EnsureCapture();
			loopbackCapture.WaitFirstProbe(1000);
			EndpointOpenFailure unavailable = loopbackCapture.Unavailable;
			if ((object)unavailable != null)
			{
				state.Fail(unavailable.SegmentReason, unavailable.MixFormat ?? loopbackCapture.MixFormat);
				loopbackCapture.RequestReprobe();
				return;
			}
			foreach (SegmentState openWriter in _openWriters)
			{
				openWriter.Writer?.FreezeMaxError();
			}
			AudioSegmentWriter audioSegmentWriter = (state.Writer = new AudioSegmentWriter(_sessionDir, state.Index, state.AnchorWallMs, state.AnchorQpc, _ring, _clock, _captureCounters, _streamFacts));
			_openWriters.Add(state);
			if (_firstWriterAt < 0)
			{
				_firstWriterAt = Environment.TickCount64;
			}
			EndInstant end = state.End;
			if ((object)end != null)
			{
				audioSegmentWriter.SetEnd(_clock.IndexFromQpc(end.Qpc), end.WallMs);
			}
		}
		catch (AudioWriterOpenException ex)
		{
			Log.Capture($"system audio writer open failed seg={state.Index} stage={ex.Stage} hr={ex.Hr}", "ProcessOpen", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\shared\\dotnet\\SystemAudio\\SystemAudioSession.cs", 443);
			state.Fail(ex.Reason);
			state.Handle.RecordOutcome(succeeded: false, ex.Reason);
		}
		catch (Exception ex2)
		{
			string text = $"0x{ex2.HResult:X8}";
			Log.Capture($"system audio writer open failed seg={state.Index} stage=unexpected hr={text}", "ProcessOpen", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\shared\\dotnet\\SystemAudio\\SystemAudioSession.cs", 450);
			string reason = "encoder_open_failed:unexpected:" + text;
			state.Fail(reason);
			state.Handle.RecordOutcome(succeeded: false, reason);
		}
	}

	private void ProcessSeal(SegmentState state)
	{
		SystemAudioSealHandle handle = state.Handle;
		if (handle != null)
		{
			AudioSegmentWriter writer = state.Writer;
			EndInstant end = state.End;
			if (writer == null)
			{
				SystemAudioFacts failedFacts = state.FailedFacts;
				handle.RecordOutcome(succeeded: false, failedFacts?.Reason);
				handle.Complete(((object)failedFacts == null) ? null : failedFacts with
				{
					EndWallMs = (((object)end != null) ? new long?(end.WallMs) : failedFacts.EndWallMs)
				}, null);
			}
			else
			{
				long num = (((object)end == null) ? _clock.NowIndex : _clock.IndexFromQpc(end.Qpc));
				writer.SetEnd(num, end?.WallMs ?? (state.AnchorWallMs + AudioClock.FramesToMs(num - _clock.IndexFromQpc(state.AnchorQpc))));
				writer.FeedTo(writer.End, 300);
				ServiceCapture();
				_openWriters.Remove(state);
				writer.BeginSeal(handle);
			}
		}
	}

	private LoopbackCapture EnsureCapture()
	{
		LoopbackCapture capture = _capture;
		if (capture != null)
		{
			if (!capture.IsDead)
			{
				if (_reopenOnNextOpen)
				{
					_reopenOnNextOpen = false;
					capture.RequestReopenOnDefault();
				}
				return capture;
			}
			ServiceCapture();
			capture.Join(DeadCaptureJoinBound);
		}
		if (_endpoint == null)
		{
			_endpoint = new AudioEndpoint();
		}
		capture = (_capture = new LoopbackCapture(_endpoint, _ring, _clock, _captureCounters, Settings.ThreadNamePrefix + "Audio"));
		_captureDeathHandled = false;
		_reopenOnNextOpen = false;
		capture.Start();
		return capture;
	}

	private void ServiceCapture()
	{
		LoopbackCapture capture = _capture;
		if (capture == null)
		{
			return;
		}
		LoopbackCapture.PendingInterruption pendingInterruption = capture.ConsumePendingInterruption();
		if ((object)pendingInterruption != null)
		{
			foreach (SegmentState openWriter in _openWriters)
			{
				openWriter.Writer?.MarkInterrupted(pendingInterruption.Reason, pendingInterruption.Hr);
			}
		}
		if (_endpoint != null && _endpoint.ConsumeDefaultChanged())
		{
			foreach (SegmentState openWriter2 in _openWriters)
			{
				openWriter2.Writer?.MarkInterrupted("default_device_changed", 0);
			}
			_reopenOnNextOpen = true;
		}
		if (!capture.IsDead || _captureDeathHandled)
		{
			return;
		}
		_captureDeathHandled = true;
		Exception failure = capture.Failure;
		if (failure == null)
		{
			return;
		}
		foreach (SegmentState openWriter3 in _openWriters)
		{
			openWriter3.Writer?.MarkInterrupted("capture_" + failure.GetType().Name, failure.HResult);
		}
	}

	private void FeedOpenWriters()
	{
		long upTo = _clock.NowIndex - 12000;
		if (_openWriters.Count == 0)
		{
			if (_capture != null)
			{
				_ring.AdvanceConsumed(upTo);
			}
		}
		else
		{
			if (!_ring.HasProducedAny && Environment.TickCount64 - _firstWriterAt < 1000)
			{
				return;
			}
			foreach (SegmentState openWriter in _openWriters)
			{
				openWriter.Writer?.Feed(upTo);
			}
		}
	}
}
