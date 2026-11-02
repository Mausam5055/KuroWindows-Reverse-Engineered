using System;

namespace ClientTelemetry;

public sealed record TelemetryConfig(Uri IngestUrl, Gate Level, bool Enabled, TimeSpan UploadInterval)
{
	public static readonly TimeSpan DefaultUploadInterval = TimeSpan.FromMinutes(30.0);

	public static readonly TimeSpan MinUploadInterval = TimeSpan.FromSeconds(60.0);

	public static readonly TimeSpan MaxUploadInterval = TimeSpan.FromHours(24.0);

	public static TelemetryConfig Defaults(Uri ingestUrl)
	{
		return new TelemetryConfig(ingestUrl, Gate.Required, Enabled: true, DefaultUploadInterval);
	}

	public static TelemetryConfig Resolve(TelemetryConfigWire? wire, bool loggingEnabled, Uri ingestUrl)
	{
		TelemetryConfig telemetryConfig = Defaults(ingestUrl);
		if ((object)wire == null)
		{
			return telemetryConfig with
			{
				Level = Gate.Standard,
				Enabled = loggingEnabled
			};
		}
		string level = wire.Level;
		Gate gate = ((level == "verbose") ? Gate.Verbose : ((!(level == "required")) ? Gate.Standard : Gate.Required));
		Gate level2 = gate;
		int? uploadIntervalS = wire.UploadIntervalS;
		TimeSpan timeSpan;
		if (uploadIntervalS.HasValue)
		{
			int valueOrDefault = uploadIntervalS.GetValueOrDefault();
			timeSpan = Clamp(TimeSpan.FromSeconds(valueOrDefault));
		}
		else
		{
			timeSpan = DefaultUploadInterval;
		}
		TimeSpan uploadInterval = timeSpan;
		return new TelemetryConfig(ingestUrl, level2, loggingEnabled, uploadInterval);
	}

	private static TimeSpan Clamp(TimeSpan v)
	{
		if (!(v < MinUploadInterval))
		{
			if (!(v > MaxUploadInterval))
			{
				return v;
			}
			return MaxUploadInterval;
		}
		return MinUploadInterval;
	}
}
