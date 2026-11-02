using System.Text.Json.Serialization;

namespace KuroWindows.RecorderCore.Segments;

public sealed class SegmentAudioFacts
{
	[JsonPropertyName("state")]
	public string State { get; set; } = "";

	[JsonPropertyName("reason")]
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public string? Reason { get; set; }

	[JsonPropertyName("file")]
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public string? File { get; set; }

	[JsonPropertyName("source")]
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public string? Source { get; set; }

	[JsonPropertyName("codec")]
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public string? Codec { get; set; }

	[JsonPropertyName("encoder")]
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public string? Encoder { get; set; }

	[JsonPropertyName("sample_rate")]
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public int? SampleRate { get; set; }

	[JsonPropertyName("channels")]
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public int? Channels { get; set; }

	[JsonPropertyName("bitrate")]
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public int? Bitrate { get; set; }

	[JsonPropertyName("bandwidth_hz")]
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public int? BandwidthHz { get; set; }

	[JsonPropertyName("frames")]
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public long? Frames { get; set; }

	[JsonPropertyName("sound_ms")]
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public long? SoundMs { get; set; }

	[JsonPropertyName("delay_samples")]
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public int? DelaySamples { get; set; }

	[JsonPropertyName("anchor_wall_ms")]
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public long? AnchorWallMs { get; set; }

	[JsonPropertyName("end_wall_ms")]
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public long? EndWallMs { get; set; }

	[JsonPropertyName("zeros_filled")]
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public long? ZerosFilled { get; set; }

	[JsonPropertyName("samples_dropped")]
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public long? SamplesDropped { get; set; }

	[JsonPropertyName("corrections")]
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public long? Corrections { get; set; }

	[JsonPropertyName("max_abs_error_us")]
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public long? MaxAbsErrorUs { get; set; }

	[JsonPropertyName("timestamp_errors")]
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public long? TimestampErrors { get; set; }

	[JsonPropertyName("discontinuities")]
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public long? Discontinuities { get; set; }

	[JsonPropertyName("device_reopens")]
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public long? DeviceReopens { get; set; }

	[JsonPropertyName("dropped_ms")]
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public long? DroppedMs { get; set; }

	[JsonPropertyName("starved_periods")]
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public long? StarvedPeriods { get; set; }

	[JsonPropertyName("mix_format")]
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public SegmentAudioMixFormat? MixFormat { get; set; }

	[JsonPropertyName("keepalive")]
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public bool? Keepalive { get; set; }

	[JsonPropertyName("seal_tail_zeros")]
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public long? SealTailZeros { get; set; }

	[JsonPropertyName("seal_overrun_samples")]
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public long? SealOverrunSamples { get; set; }

	[JsonPropertyName("interrupted_at_ms")]
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public long? InterruptedAtMs { get; set; }
}
