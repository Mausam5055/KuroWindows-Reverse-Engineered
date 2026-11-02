namespace ClientTelemetry;

internal static class Catalog
{
	public static TelemetryEvent CountersFlushed(Attributes a)
	{
		return new TelemetryEvent("counters.flushed", Kind.Counters, Severity.Info, Gate.Required, a);
	}
}
