namespace SystemAudio;

internal static class Settings
{
	private static SystemAudioSettings Current => SystemAudioRuntime.Current;

	public static string LatchDirectory => Current.LatchDirectory;

	public static string LatchFileName => Current.LatchFileName;

	public static string AppVersion => Current.AppVersion;

	public static string OsVersion => Current.OsVersion;

	public static string ThreadNamePrefix => Current.ThreadNamePrefix;
}
