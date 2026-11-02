using System;
using System.Diagnostics;
using KuroWindows.RecorderCore.Capture;

namespace KuroWindows.Capture;

internal sealed class CaptureCadenceController
{
	public const int ActiveLookbackMs = 1000;

	private readonly long _targetIntervalQpc;

	private readonly PadCommitPolicy _padPolicy;

	private long _nextDueQpc;

	private bool _wasActive;

	public CaptureCadenceController(int targetFps, PadCommitPolicy? padPolicy = null)
	{
		_targetIntervalQpc = Math.Max(1L, Stopwatch.Frequency / Math.Max(targetFps, 1));
		_padPolicy = padPolicy ?? new PadCommitPolicy(targetFps);
	}

	public CaptureCadenceDecision OnWake(long nowQpc, bool hasFirstFrame, bool hasPendingFrame, bool hasRecentActivity, PadCursorObservation cursor = default(PadCursorObservation))
	{
		bool flag = hasRecentActivity && !_wasActive;
		_wasActive = hasRecentActivity;
		if (_nextDueQpc != 0L && nowQpc < _nextDueQpc && !flag)
		{
			return CaptureCadenceDecision.NotDue;
		}
		long num = ((_nextDueQpc == 0L) ? 0 : (nowQpc - _nextDueQpc));
		bool flag2 = num > 5 * _targetIntervalQpc;
		long num2 = ((flag2 || _nextDueQpc == 0L) ? nowQpc : _nextDueQpc);
		_nextDueQpc = num2 + _targetIntervalQpc;
		if (!hasFirstFrame && !hasPendingFrame)
		{
			return new CaptureCadenceDecision(PadCommitKind.None, flag2, LateMs(num));
		}
		if (hasPendingFrame)
		{
			_padPolicy.NotePendingFrameTick(cursor);
			return new CaptureCadenceDecision(PadCommitKind.PendingFrame, flag2, LateMs(num));
		}
		PadCommitDecision padCommitDecision = _padPolicy.DecideNoPendingTick(nowQpc, hasRecentActivity, cursor);
		return new CaptureCadenceDecision(padCommitDecision.Kind, flag2, LateMs(num), padCommitDecision.CursorBlendChanged);
	}

	public void MarkCommitted(long qpc)
	{
		_padPolicy.MarkCommitted(qpc);
	}

	private static long LateMs(long lateByQpc)
	{
		if (lateByQpc > 0)
		{
			return lateByQpc * 1000 / Stopwatch.Frequency;
		}
		return 0L;
	}
}
