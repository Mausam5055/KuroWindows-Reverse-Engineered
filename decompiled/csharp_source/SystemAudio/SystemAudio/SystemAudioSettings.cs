namespace SystemAudio;

public sealed record SystemAudioSettings(string LatchDirectory, string LatchFileName, string AppVersion, string OsVersion, string? EnvironmentOverrideRaw, string ThreadNamePrefix);
