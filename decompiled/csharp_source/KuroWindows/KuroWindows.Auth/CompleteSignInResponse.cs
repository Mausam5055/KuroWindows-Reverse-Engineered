using System.Text.Json.Serialization;

namespace KuroWindows.Auth;

public record CompleteSignInResponse
{
	[JsonPropertyName("status")]
	public string Status { get; init; } = "ok";

	[JsonPropertyName("profile")]
	public CompleteSignInProfile? Profile { get; init; }
}
