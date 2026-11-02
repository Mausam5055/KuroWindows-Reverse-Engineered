using System.Text.Json.Serialization;

namespace DeviceIdentity;

public record DeviceChipClaim
{
	[JsonPropertyName("ek_sha256")]
	public string EkSha256 { get; init; } = "";

	[JsonPropertyName("ek_certificate")]
	public string? EkCertificate { get; init; }

	[JsonPropertyName("source")]
	public string Source { get; init; } = "none";
}
