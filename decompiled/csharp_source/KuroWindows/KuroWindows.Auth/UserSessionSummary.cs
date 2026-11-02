using System;
using System.Text.Json.Serialization;

namespace KuroWindows.Auth;

public record UserSessionSummary
{
	[JsonPropertyName("user_id")]
	public string UserId { get; init; } = "";

	[JsonPropertyName("email")]
	public string Email { get; init; } = "";

	[JsonPropertyName("access_token")]
	public string AccessToken { get; init; } = "";

	[JsonPropertyName("refresh_token")]
	public string RefreshToken { get; init; } = "";

	[JsonPropertyName("expires_at")]
	public DateTimeOffset ExpiresAt { get; init; }
}
