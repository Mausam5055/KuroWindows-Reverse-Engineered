using System.Text.Json.Serialization;

namespace KuroWindows.Auth;

public record DesktopDashboardRequest
{
	[JsonPropertyName("action")]
	public string Action { get; init; } = "";

	[JsonPropertyName("params")]
	public object Params { get; init; } = new object();
}
