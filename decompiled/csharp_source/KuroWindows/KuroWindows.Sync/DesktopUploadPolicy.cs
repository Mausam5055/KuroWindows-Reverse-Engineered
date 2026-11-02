using System.Text.Json.Serialization;

namespace KuroWindows.Sync;

public sealed record DesktopUploadPolicy
{
	[JsonPropertyName("backlog_purge_hours")]
	public double? BacklogPurgeHours { get; init; }

	[JsonPropertyName("completion_region")]
	public string? CompletionRegion { get; init; }
}
