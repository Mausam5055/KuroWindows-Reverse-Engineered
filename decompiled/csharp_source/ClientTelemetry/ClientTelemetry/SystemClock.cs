using System;
using System.Diagnostics;

namespace ClientTelemetry;

internal sealed class SystemClock : ITelemetryClock
{
	private readonly Stopwatch _stopwatch = Stopwatch.StartNew();

	public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

	public long MonoMs => _stopwatch.ElapsedMilliseconds;
}
