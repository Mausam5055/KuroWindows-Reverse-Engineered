using System;

namespace ClientTelemetry;

public interface ITelemetryClock
{
	DateTimeOffset UtcNow { get; }

	long MonoMs { get; }
}
