namespace SystemAudio;

internal struct LibInfo
{
	public const int ModuleLast = 39;

	public const int ModuleAacEnc = 4;

	public nint Title;

	public nint BuildDate;

	public nint BuildTime;

	public int ModuleId;

	public int Version;

	public uint Flags;

	public unsafe fixed byte VersionStr[32];
}
