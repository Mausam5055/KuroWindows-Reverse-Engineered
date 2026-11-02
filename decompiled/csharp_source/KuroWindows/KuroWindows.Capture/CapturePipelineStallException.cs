namespace KuroWindows.Capture;

internal sealed class CapturePipelineStallException : CaptureEncoderStallException
{
	public const string StageCommitWedge = "commit_wedge";

	public const string StageFinalizeTimeout = "finalize_timeout";

	public const string StageSealLockTimeout = "seal_lock_timeout";

	public const string StageInitialFrame = "initial_frame";

	public const string StagePoolStarvation = "pool_starvation";

	public double SilentSeconds { get; }

	public string Stage { get; }

	public override bool LatchesWireProfile => false;

	public CapturePipelineStallException(int displayIndex, double silentSeconds, string stage, string reason)
		: base(displayIndex, reason)
	{
		SilentSeconds = silentSeconds;
		Stage = stage;
	}
}
