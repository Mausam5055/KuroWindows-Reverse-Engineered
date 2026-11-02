using System.Text.Json.Serialization;

namespace KuroWindows.CoreBridge;

public sealed record CoreBridgeMaskingState
{
	[JsonPropertyName("status")]
	public string Status { get; init; } = "normal";

	[JsonPropertyName("context_type")]
	public string? ContextType { get; init; }

	[JsonPropertyName("context_detail")]
	public string? ContextDetail { get; init; }

	[JsonPropertyName("context_app_name")]
	public string? ContextAppName { get; init; }

	[JsonPropertyName("mode")]
	public string Mode { get; init; } = "default";

	[JsonPropertyName("blocked_app_count")]
	public int BlockedAppCount { get; init; }

	[JsonPropertyName("sensitive_browser_title_keyword_count")]
	public int SensitiveBrowserTitleKeywordCount { get; init; }

	[JsonPropertyName("excluded_browser_title_keyword_count")]
	public int ExcludedBrowserTitleKeywordCount { get; init; }

	[JsonPropertyName("capture_contention")]
	public bool CaptureContention { get; init; }
}
