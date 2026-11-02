using System.Text.Json.Serialization;
using KuroWindows.Auth;

namespace KuroWindows.CoreBridge;

public sealed record CoreBridgeAuthState
{
	[JsonPropertyName("state")]
	public string State { get; init; } = AuthState.SignedOut.ToString();

	[JsonPropertyName("signed_in")]
	public bool SignedIn { get; init; }

	[JsonPropertyName("session_generation")]
	public int? SessionGeneration { get; init; }

	[JsonPropertyName("user_id")]
	public string? UserId { get; init; }

	[JsonPropertyName("email")]
	public string? Email { get; init; }

	[JsonPropertyName("ban_reason")]
	public string? BanReason { get; init; }

	[JsonPropertyName("linked_email")]
	public string? LinkedEmail { get; init; }

	[JsonPropertyName("last_error")]
	public string? LastError { get; init; }

	[JsonPropertyName("last_error_code")]
	public string? LastErrorCode { get; init; }
}
