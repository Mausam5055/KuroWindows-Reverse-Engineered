using System;
using System.Diagnostics;
using System.Threading;
using KuroWindows.RecorderCore.Clock;
using KuroWindows.Sync;

namespace KuroWindows.RecorderCore.Capture;

internal sealed class PadCommitPolicy
{
	public static readonly long HeartbeatIntervalQpc = Stopwatch.Frequency;

	private static int _loggedVersion;

	private readonly long _padMinSpacingQpc;

	private readonly Func<bool> _legacyPadCadence;

	private long _lastCommittedQpc;

	private PadCursorObservation _committedCursor;

	private bool _hasCommittedCursor;

	private PadCursorObservation _lastObservedCursor;

	private bool _hasObservedCursor;

	public static bool SeededLegacyPadCadence()
	{
		return DesktopFeatureFlags.Seeded.LegacyPadCadence;
	}

	public PadCommitPolicy(int targetFps, Func<bool>? legacyPadCadence = null)
	{
		long num = Math.Max(1L, Stopwatch.Frequency / Math.Max(targetFps, 1));
		_padMinSpacingQpc = Math.Max(1L, num - num / 8);
		_legacyPadCadence = legacyPadCadence ?? new Func<bool>(SeededLegacyPadCadence);
	}

	public void NotePendingFrameTick(PadCursorObservation cursor)
	{
		Observe(cursor);
	}

	public PadCommitDecision DecideNoPendingTick(long nowQpc, bool hasRecentActivity, PadCursorObservation cursor)
	{
		Observe(cursor);
		long num = ((_lastCommittedQpc == 0L) ? (-1) : (nowQpc - _lastCommittedQpc));
		bool flag = CursorChanged(cursor);
		if (_legacyPadCadence())
		{
			LogVersionTransition(legacy: true);
			if (!hasRecentActivity && num >= 0 && num < HeartbeatIntervalQpc)
			{
				return PadCommitDecision.None;
			}
			return new PadCommitDecision(PadCommitKind.BlendedPad, flag);
		}
		LogVersionTransition(legacy: false);
		if (flag && (num < 0 || num >= _padMinSpacingQpc))
		{
			return new PadCommitDecision(PadCommitKind.BlendedPad, flag);
		}
		if (num >= HeartbeatIntervalQpc)
		{
			return new PadCommitDecision(PadCommitKind.PixelTrueHeartbeat, CursorBlendChanged: false);
		}
		return PadCommitDecision.None;
	}

	public void MarkCommitted(long qpc)
	{
		_lastCommittedQpc = qpc;
		if (_hasObservedCursor)
		{
			_committedCursor = _lastObservedCursor;
			_hasCommittedCursor = true;
		}
	}

	public static PadFrameStamp CreatePadStamp(long nowQpc)
	{
		return new PadFrameStamp(nowQpc, RecordingClock.Shared.WallMsFromQpc(nowQpc), TimestampKind.DuplicateQpc, TimestampQuality.Degraded, CaptureFrameKind.Duplicate);
	}

	private bool CursorChanged(PadCursorObservation cursor)
	{
		if (!cursor.Known)
		{
			return false;
		}
		if (!_hasCommittedCursor)
		{
			return true;
		}
		if (cursor.X == _committedCursor.X && cursor.Y == _committedCursor.Y)
		{
			return cursor.SpriteHandle != _committedCursor.SpriteHandle;
		}
		return true;
	}

	private void Observe(PadCursorObservation cursor)
	{
		if (cursor.Known)
		{
			_lastObservedCursor = cursor;
			_hasObservedCursor = true;
		}
	}

	private static void LogVersionTransition(bool legacy)
	{
		int num = (legacy ? 1 : 2);
		int num2 = Volatile.Read(in _loggedVersion);
		if (num2 != num && Interlocked.CompareExchange(ref _loggedVersion, num, num2) == num2)
		{
			KuroLog.Capture($"pad_policy version=v{num}", "LogVersionTransition", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\RecorderCore\\Capture\\PadCommitPolicy.cs", 389);
		}
	}
}
