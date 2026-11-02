using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace KuroWindows.RecorderCore.Segments;

public sealed class SegmentManifest
{
	[JsonPropertyName("schema")]
	public int Schema { get; set; } = 1;

	[JsonPropertyName("state")]
	public SegmentManifestState State { get; set; }

	[JsonPropertyName("session_id")]
	public string SessionId { get; set; } = "";

	[JsonPropertyName("segment_index")]
	public int SegmentIndex { get; set; }

	[JsonPropertyName("started_wall_ms")]
	public long StartedWallMs { get; set; }

	[JsonPropertyName("ended_wall_ms")]
	public long? EndedWallMs { get; set; }

	[JsonPropertyName("claimed_duration_ms")]
	public long? ClaimedDurationMs { get; set; }

	[JsonPropertyName("end_reason")]
	public string? EndReason { get; set; }

	[JsonPropertyName("end_source")]
	public string? EndSource { get; set; }

	[JsonPropertyName("mask_trigger")]
	public string? MaskTrigger { get; set; }

	[JsonPropertyName("mask_detail")]
	public string? MaskDetail { get; set; }

	[JsonPropertyName("topology_mode")]
	public string? TopologyMode { get; set; }

	[JsonPropertyName("topology_epoch")]
	public int? TopologyEpoch { get; set; }

	[JsonPropertyName("topology_signature")]
	public string? TopologySignature { get; set; }

	[JsonPropertyName("physical_display_count")]
	public int? PhysicalDisplayCount { get; set; }

	[JsonPropertyName("logical_recorded_display_count")]
	public int? LogicalRecordedDisplayCount { get; set; }

	[JsonPropertyName("topology_unsupported_reason")]
	public string? TopologyUnsupportedReason { get; set; }

	[JsonPropertyName("adaptive_profile")]
	public string? AdaptiveProfile { get; set; }

	[JsonPropertyName("adaptive_profile_index")]
	public int? AdaptiveProfileIndex { get; set; }

	[JsonPropertyName("adaptive_profile_reason")]
	public string? AdaptiveProfileReason { get; set; }

	[JsonPropertyName("adaptive_profile_failure_count")]
	public int? AdaptiveProfileFailureCount { get; set; }

	[JsonPropertyName("end_error")]
	public SegmentManifestError? EndError { get; set; }

	[JsonPropertyName("quarantine_reason")]
	public string? QuarantineReason { get; set; }

	[JsonPropertyName("audio_file")]
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public string? AudioFile { get; set; }

	[JsonPropertyName("audio_bytes")]
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public long? AudioBytes { get; set; }

	[JsonPropertyName("audio_sha256")]
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public string? AudioSha256 { get; set; }

	[JsonPropertyName("audio")]
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public SegmentAudioFacts? Audio { get; set; }

	[JsonPropertyName("session_facts")]
	public SegmentSessionFacts? SessionFacts { get; set; }

	[JsonPropertyName("displays")]
	public List<SegmentDisplayManifest> Displays { get; set; } = new List<SegmentDisplayManifest>();
}
