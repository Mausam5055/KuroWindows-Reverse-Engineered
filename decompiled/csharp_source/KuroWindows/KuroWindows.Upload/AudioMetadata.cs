using System.Text.Json.Serialization;

namespace KuroWindows.Upload;

internal sealed record AudioMetadata
{
	[JsonPropertyName("state")]
	public string State { get; init; } = "";

	[JsonPropertyName("reason")]
	public string? Reason { get; init; }

	[JsonPropertyName("file")]
	public string? File { get; init; }

	[JsonPropertyName("source")]
	public string? Source { get; init; }

	[JsonPropertyName("codec")]
	public string? Codec { get; init; }

	[JsonPropertyName("encoder")]
	public string? Encoder { get; init; }

	[JsonPropertyName("sample_rate")]
	public int? SampleRate { get; init; }

	[JsonPropertyName("channels")]
	public int? Channels { get; init; }

	[JsonPropertyName("bitrate")]
	public int? Bitrate { get; init; }

	[JsonPropertyName("bandwidth_hz")]
	public int? BandwidthHz { get; init; }

	[JsonPropertyName("frames")]
	public long? Frames { get; init; }

	[JsonPropertyName("sound_ms")]
	public long? SoundMs { get; init; }

	[JsonPropertyName("delay_samples")]
	public int? DelaySamples { get; init; }

	[JsonPropertyName("anchor_wall_ms")]
	public long? AnchorWallMs { get; init; }

	[JsonPropertyName("end_wall_ms")]
	public long? EndWallMs { get; init; }

	[JsonPropertyName("zeros_filled")]
	public long? ZerosFilled { get; init; }

	[JsonPropertyName("samples_dropped")]
	public long? SamplesDropped { get; init; }

	[JsonPropertyName("corrections")]
	public long? Corrections { get; init; }

	[JsonPropertyName("max_abs_error_us")]
	public long? MaxAbsErrorUs { get; init; }

	[JsonPropertyName("timestamp_errors")]
	public long? TimestampErrors { get; init; }

	[JsonPropertyName("discontinuities")]
	public long? Discontinuities { get; init; }

	[JsonPropertyName("device_reopens")]
	public long? DeviceReopens { get; init; }

	[JsonPropertyName("dropped_ms")]
	public long? DroppedMs { get; init; }

	[JsonPropertyName("starved_periods")]
	public long? StarvedPeriods { get; init; }

	[JsonPropertyName("mix_format")]
	public AudioMixFormatMetadata? MixFormat { get; init; }

	[JsonPropertyName("keepalive")]
	public bool? Keepalive { get; init; }

	[JsonPropertyName("seal_tail_zeros")]
	public long? SealTailZeros { get; init; }

	[JsonPropertyName("seal_overrun_samples")]
	public long? SealOverrunSamples { get; init; }

	[JsonPropertyName("interrupted_at_ms")]
	public long? InterruptedAtMs { get; init; }

	[JsonPropertyName("first_frame_offset_us")]
	public long? FirstFrameOffsetUs { get; init; }
}
