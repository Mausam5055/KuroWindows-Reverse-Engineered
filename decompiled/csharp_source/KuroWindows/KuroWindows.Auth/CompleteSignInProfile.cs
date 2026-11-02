using System.Text.Json.Serialization;

namespace KuroWindows.Auth;

public record CompleteSignInProfile
{
	[JsonPropertyName("id")]
	public string Id { get; init; } = "";

	[JsonPropertyName("email")]
	public string Email { get; init; } = "";

	[JsonPropertyName("full_name")]
	public string? FullName { get; init; }

	[JsonPropertyName("role")]
	public string Role { get; init; } = "user";

	[JsonPropertyName("status")]
	public string Status { get; init; } = "active";
}
