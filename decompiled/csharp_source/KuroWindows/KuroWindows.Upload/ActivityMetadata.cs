using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace KuroWindows.Upload;

internal sealed class ActivityMetadata
{
	[JsonPropertyName("total_clicks")]
	public int TotalClicks { get; set; }

	[JsonPropertyName("total_keystrokes")]
	public int TotalKeystrokes { get; set; }

	[JsonPropertyName("total_scrolls")]
	public int TotalScrolls { get; set; }

	[JsonPropertyName("app_usage")]
	public IReadOnlyDictionary<string, double>? AppUsage { get; set; }
}
