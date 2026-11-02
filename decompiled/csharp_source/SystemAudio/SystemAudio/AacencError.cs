namespace SystemAudio;

internal static class AacencError
{
	public const uint Ok = 0u;

	public const uint InvalidHandle = 32u;

	public const uint MemoryError = 33u;

	public const uint UnsupportedParameter = 34u;

	public const uint InvalidConfig = 35u;

	public const uint InitError = 64u;

	public const uint InitAacError = 65u;

	public const uint InitSbrError = 66u;

	public const uint InitTpError = 67u;

	public const uint InitMetaError = 68u;

	public const uint InitMpsError = 69u;

	public const uint EncodeError = 96u;

	public const uint EncodeEof = 128u;
}
