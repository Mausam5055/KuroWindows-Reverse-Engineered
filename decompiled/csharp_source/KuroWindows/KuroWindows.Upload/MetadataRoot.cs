using System.Text.Json.Serialization;

namespace KuroWindows.Upload;

internal sealed class MetadataRoot
{
	[JsonPropertyName("session")]
	public SessionMetadata Session { get; set; } = new SessionMetadata();

	[JsonPropertyName("device")]
	public DeviceMetadata Device { get; set; } = new DeviceMetadata();

	[JsonPropertyName("segment")]
	public SegmentMetadata Segment { get; set; } = new SegmentMetadata();

	[JsonPropertyName("activity")]
	public ActivityMetadata Activity { get; set; } = new ActivityMetadata();

	[JsonPropertyName("files")]
	public FilesMetadata Files { get; set; } = new FilesMetadata();

	[JsonPropertyName("integrity")]
	public IntegrityMetadata Integrity { get; set; } = new IntegrityMetadata();

	[JsonPropertyName("segment_end")]
	public SegmentEndMetadata? SegmentEnd { get; set; }

	[JsonPropertyName("session_facts")]
	public SessionFactsMetadata? SessionFacts { get; set; }

	[JsonPropertyName("audio")]
	public AudioMetadata? Audio { get; set; }
}
