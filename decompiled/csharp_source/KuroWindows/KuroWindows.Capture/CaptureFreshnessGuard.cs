using System;
using System.Collections.Generic;
using KuroWindows.RecorderCore.Clock;

namespace KuroWindows.Capture;

internal sealed class CaptureFreshnessGuard
{
	private readonly record struct Observation(TimestampKind TimestampKind, int ActiveInputEvents, long PtsNs, long DurationNs)
	{
		public long EndNs => PtsNs + DurationNs;
	}

	private readonly int _displayIndex;

	private readonly int _captureFps;

	private readonly Queue<Observation> _window = new Queue<Observation>();

	private long _encodedDurationNs;

	private long _sourceDurationNs;

	private long _fillerDurationNs;

	private long _activeInputEvents;

	public CaptureFreshnessGuard(int displayIndex, int captureFps)
	{
		_displayIndex = displayIndex;
		_captureFps = Math.Max(captureFps, 1);
	}

	public string? Observe(TimestampKind timestampKind, int activeInputEvents, long ptsNs, long durationNs)
	{
		Observation observation = new Observation(timestampKind, activeInputEvents, ptsNs, (durationNs > 0) ? durationNs : (1000000000L / (long)_captureFps));
		_window.Enqueue(observation);
		Add(observation);
		TrimWindow(observation.EndNs);
		if (_window.Count <= 0)
		{
			return null;
		}
		if (_activeInputEvents < 500 || (_encodedDurationNs + 999999) / 1000000 < 120000)
		{
			return null;
		}
		if (_sourceDurationNs <= 0)
		{
			return $"capture surface stale for display {_displayIndex}: {_activeInputEvents} active input events but no source desktop frames";
		}
		if (_fillerDurationNs * 100 >= _encodedDurationNs * 98)
		{
			return $"capture surface stale for display {_displayIndex}: {_activeInputEvents} active input events while {_fillerDurationNs / 1000000}/{_encodedDurationNs / 1000000}ms reused the last source frame";
		}
		return null;
	}

	private static bool IsFiller(TimestampKind timestampKind)
	{
		return timestampKind == TimestampKind.DuplicateQpc;
	}

	private void TrimWindow(long currentEndNs)
	{
		long num = currentEndNs - 120000000000L;
		while (_window.Count > 1 && _window.Peek().EndNs <= num)
		{
			Remove(_window.Dequeue());
		}
	}

	private void Add(Observation observation)
	{
		_encodedDurationNs += observation.DurationNs;
		_activeInputEvents += observation.ActiveInputEvents;
		if (IsFiller(observation.TimestampKind))
		{
			_fillerDurationNs += observation.DurationNs;
		}
		else
		{
			_sourceDurationNs += observation.DurationNs;
		}
	}

	private void Remove(Observation observation)
	{
		_encodedDurationNs -= observation.DurationNs;
		_activeInputEvents -= observation.ActiveInputEvents;
		if (IsFiller(observation.TimestampKind))
		{
			_fillerDurationNs -= observation.DurationNs;
		}
		else
		{
			_sourceDurationNs -= observation.DurationNs;
		}
	}
}
