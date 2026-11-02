using System.Text.Json;
using System.Text.Json.Serialization;

namespace KuroWindows.Auth;

public record DesktopDashboardEnvelope
{
	[JsonPropertyName("ok")]
	public bool Ok { get; init; }

	[JsonPropertyName("data")]
	public JsonElement Data { get; init; }
}
