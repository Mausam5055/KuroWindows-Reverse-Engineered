using System.Text.Json.Serialization;

namespace KuroWindows.Auth;

public record SupabaseUser
{
	[JsonPropertyName("id")]
	public string Id { get; init; } = "";

	[JsonPropertyName("email")]
	public string? Email { get; init; }
}
