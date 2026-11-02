namespace DesktopUpdates;

public readonly record struct UpdateSnapshot(bool Required, string Status, string? AvailableVersion);
