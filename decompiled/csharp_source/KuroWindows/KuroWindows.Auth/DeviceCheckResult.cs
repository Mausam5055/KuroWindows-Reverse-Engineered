using System.Text.Json.Serialization;

namespace KuroWindows.Auth;

public record DeviceCheckResult
{
	[JsonPropertyName("status")]
	public string Status { get; init; } = "ok";

	[JsonPropertyName("device_id")]
	public string? DeviceId { get; init; }

	[JsonPropertyName("ban_reason")]
	public string? BanReason { get; init; }

	[JsonPropertyName("linked_email")]
	public string? LinkedEmail { get; init; }

	[JsonPropertyName("key_status")]
	public string? KeyStatus { get; init; }

	[JsonIgnore]
	public bool Signed { get; init; }
}
