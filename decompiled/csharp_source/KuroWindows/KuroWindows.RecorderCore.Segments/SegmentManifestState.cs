namespace KuroWindows.RecorderCore.Segments;

public enum SegmentManifestState
{
	Recording,
	Finalizing,
	Complete,
	Suspect,
	Recovered,
	Quarantined
}
