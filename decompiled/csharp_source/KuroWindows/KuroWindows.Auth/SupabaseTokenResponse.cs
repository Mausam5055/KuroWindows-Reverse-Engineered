using System.Text.Json.Serialization;

namespace KuroWindows.Auth;

public record SupabaseTokenResponse
{
	[JsonPropertyName("access_token")]
	public string AccessToken { get; init; } = "";

	[JsonPropertyName("refresh_token")]
	public string RefreshToken { get; init; } = "";

	[JsonPropertyName("expires_in")]
	public int ExpiresIn { get; init; }

	[JsonPropertyName("user")]
	public SupabaseUser? User { get; init; }
}
