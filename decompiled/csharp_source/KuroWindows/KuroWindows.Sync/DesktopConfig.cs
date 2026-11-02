using System.Text.Json.Serialization;
using ClientTelemetry;

namespace KuroWindows.Sync;

public sealed record DesktopConfig
{
	[JsonPropertyName("masking")]
	public DesktopMaskingPolicy Masking { get; init; } = new DesktopMaskingPolicy();

	[JsonPropertyName("logging_enabled")]
	public bool LoggingEnabled { get; init; }

	[JsonPropertyName("telemetry")]
	public TelemetryConfigWire? Telemetry { get; init; }

	[JsonPropertyName("video")]
	public DesktopVideoPolicy? Video { get; init; }

	[JsonPropertyName("video_source")]
	public string? VideoSource { get; init; }

	[JsonPropertyName("upload")]
	public DesktopUploadPolicy? Upload { get; init; }

	[JsonPropertyName("recorder_core")]
	public string? RecorderCore { get; init; }

	[JsonPropertyName("recorder_core_source")]
	public string? RecorderCoreSource { get; init; }

	[JsonPropertyName("feature_flags")]
	public DesktopFeatureFlags? FeatureFlags { get; init; }

	public DesktopConfig Normalize()
	{
		return this with
		{
			Masking = Masking.Normalize(),
			FeatureFlags = FeatureFlags?.Normalize()
		};
	}

	public bool TryValidate(out string error)
	{
		if (!Masking.TryValidate(out error))
		{
			return false;
		}
		error = "";
		return true;
	}
}
