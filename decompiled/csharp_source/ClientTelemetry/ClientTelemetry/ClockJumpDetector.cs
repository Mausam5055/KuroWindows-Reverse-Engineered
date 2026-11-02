using System;

namespace ClientTelemetry;

internal sealed class ClockJumpDetector
{
	private const long ThresholdMs = 2000L;

	private const long ReportIntervalMs = 60000L;

	private long? _lastWallMs;

	private long? _lastMonoMs;

	private long? _lastReportMonoMs;

	public bool Observe(long wallMs, long monoMs, out long wallDeltaMs, out long monoDeltaMs)
	{
		wallDeltaMs = 0L;
		monoDeltaMs = 0L;
		bool result = false;
		long? lastWallMs = _lastWallMs;
		if (lastWallMs.HasValue)
		{
			long valueOrDefault = lastWallMs.GetValueOrDefault();
			lastWallMs = _lastMonoMs;
			if (lastWallMs.HasValue)
			{
				long valueOrDefault2 = lastWallMs.GetValueOrDefault();
				wallDeltaMs = wallMs - valueOrDefault;
				monoDeltaMs = monoMs - valueOrDefault2;
				if (Math.Abs(wallDeltaMs - monoDeltaMs) > 2000)
				{
					lastWallMs = _lastReportMonoMs;
					if (!lastWallMs.HasValue || monoMs - _lastReportMonoMs.Value >= 60000)
					{
						result = true;
						_lastReportMonoMs = monoMs;
					}
				}
			}
		}
		_lastWallMs = wallMs;
		_lastMonoMs = monoMs;
		return result;
	}
}
