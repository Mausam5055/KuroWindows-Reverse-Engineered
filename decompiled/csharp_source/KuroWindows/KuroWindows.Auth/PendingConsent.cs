using System.Text.Json.Serialization;

namespace KuroWindows.Auth;

public sealed record PendingConsent([property: JsonPropertyName("userId")] string UserId, [property: JsonPropertyName("documentType")] string DocumentType, [property: JsonPropertyName("documentVersion")] string DocumentVersion, [property: JsonPropertyName("ageConfirmed")] bool AgeConfirmed);
