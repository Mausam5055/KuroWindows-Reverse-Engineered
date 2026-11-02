using System.Text.Json.Serialization;

namespace KuroWindows.Upload;

internal sealed class IntegrityMetadata
{
	[JsonPropertyName("synthetic_event_count")]
	public int SyntheticEventCount { get; set; }

	[JsonPropertyName("total_input_event_count")]
	public int TotalInputEventCount { get; set; }

	[JsonPropertyName("timing_cv")]
	public double? TimingCv { get; set; }

	[JsonPropertyName("jiggler_detected")]
	public bool JigglerDetected { get; set; }

	[JsonPropertyName("jiggler_app_name")]
	public string? JigglerAppName { get; set; }

	[JsonPropertyName("timing_validation")]
	public TimingValidationMetadata? TimingValidation { get; set; }

	[JsonPropertyName("capture_freshness")]
	public CaptureFreshnessMetadata? CaptureFreshness { get; set; }
}
