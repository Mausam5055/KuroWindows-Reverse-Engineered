using System.Text.Json.Serialization;

namespace ClientTelemetry;

public sealed record TelemetryConfigWire
{
	[JsonPropertyName("level")]
	public string? Level { get; init; }

	[JsonPropertyName("upload_interval_s")]
	public int? UploadIntervalS { get; init; }
}
