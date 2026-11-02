namespace SystemAudio;

public sealed record SystemAudioFacts
{
	public required string State { get; init; }

	public string? Reason { get; init; }

	public string? File { get; init; }

	public string? Source { get; init; }

	public string? Codec { get; init; }

	public string? Encoder { get; init; }

	public int? SampleRate { get; init; }

	public int? Channels { get; init; }

	public int? Bitrate { get; init; }

	public int? BandwidthHz { get; init; }

	public long? Frames { get; init; }

	public long? SoundMs { get; init; }

	public int? DelaySamples { get; init; }

	public long? AnchorWallMs { get; init; }

	public long? EndWallMs { get; init; }

	public long? ZerosFilled { get; init; }

	public long? SamplesDropped { get; init; }

	public long? Corrections { get; init; }

	public long? MaxAbsErrorUs { get; init; }

	public long? TimestampErrors { get; init; }

	public long? Discontinuities { get; init; }

	public long? DeviceReopens { get; init; }

	public long? DroppedMs { get; init; }

	public long? StarvedPeriods { get; init; }

	public SystemAudioMixFormat? MixFormat { get; init; }

	public bool? Keepalive { get; init; }

	public long? SealTailZeros { get; init; }

	public long? SealOverrunSamples { get; init; }

	public long? InterruptedAtMs { get; init; }
}
