using System.Text.Json.Serialization;

namespace KuroWindows.Upload;

internal sealed class FilesMetadata
{
	[JsonPropertyName("clip_file")]
	public string ClipFile { get; set; } = "";

	[JsonPropertyName("clip_size_bytes")]
	public long ClipSizeBytes { get; set; }

	[JsonPropertyName("events_file")]
	public string? EventsFile { get; set; }

	[JsonPropertyName("events_size_bytes")]
	public long? EventsSizeBytes { get; set; }

	[JsonPropertyName("audio_file")]
	public string? AudioFile { get; set; }

	[JsonPropertyName("audio_size_bytes")]
	public long? AudioSizeBytes { get; set; }
}
