namespace ClientTelemetry;

public static class Observe
{
	public static void UploadLatencyS(double value)
	{
		Telemetry.Observe("upload_latency_s", value);
	}

	public static void UploadPauseS(double value)
	{
		Telemetry.Observe("upload_pause_s", value);
	}

	public static void MemoryPrivateMb(double value)
	{
		Telemetry.Observe("memory_private_mb", value);
	}

	public static void EncodeP95Ms(double value)
	{
		Telemetry.Observe("encode_p95_ms", value);
	}

	public static void AxWalkMs(double value)
	{
		Telemetry.Observe("ax_walk_ms", value);
	}

	public static void AxChainLagMs(double value)
	{
		Telemetry.Observe("ax_chain_lag_ms", value);
	}
}
