using System;

namespace SystemAudio;

public interface ISystemAudio : IDisposable
{
	string PolicyDescription { get; }

	void PostOpen(int segmentIndex, long anchorWallMs, long anchorQpc);

	void PostEnd(int segmentIndex, long endWallMs, long endQpc);

	SystemAudioSealHandle PostSeal(int segmentIndex, long endWallMs, long endQpc);

	SystemAudioFacts? LiveFacts(int segmentIndex);
}
