using System.Text.Json.Serialization;

namespace KuroWindows.CoreBridge;

public sealed record CoreBridgeSettingsState
{
	[JsonPropertyName("launch_at_startup")]
	public bool LaunchAtStartup { get; init; }

	[JsonPropertyName("launch_at_startup_registered")]
	public bool LaunchAtStartupRegistered { get; init; }

	[JsonPropertyName("launch_at_startup_error")]
	public string? LaunchAtStartupError { get; init; }

	[JsonPropertyName("onboarding_completed")]
	public bool OnboardingCompleted { get; init; }

	[JsonPropertyName("theme")]
	public string Theme { get; init; } = "light";
}
