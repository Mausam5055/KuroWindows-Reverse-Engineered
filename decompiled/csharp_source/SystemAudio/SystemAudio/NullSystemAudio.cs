using System;

namespace SystemAudio;

public sealed class NullSystemAudio : ISystemAudio, IDisposable
{
	public static readonly NullSystemAudio Instance = new NullSystemAudio();

	public string PolicyDescription => "off:null";

	private NullSystemAudio()
	{
	}

	public void PostOpen(int segmentIndex, long anchorWallMs, long anchorQpc)
	{
	}

	public void PostEnd(int segmentIndex, long endWallMs, long endQpc)
	{
	}

	public SystemAudioSealHandle PostSeal(int segmentIndex, long endWallMs, long endQpc)
	{
		return SystemAudioSealHandle.None;
	}

	public SystemAudioFacts? LiveFacts(int segmentIndex)
	{
		return null;
	}

	public void Dispose()
	{
	}
}
