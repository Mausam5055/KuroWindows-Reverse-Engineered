namespace KuroWindows.RecorderCore.Capture;

internal static class PipelineStallPolicy
{
	public const int FinalizeTimeoutSeconds = 20;

	public const int SealLockTimeoutSeconds = 20;

	public const int PipelineStallSeconds = 25;

	public const int EncodeTaskStopTimeoutSeconds = 2;

	public const int EncodeTaskStopWedgedCaptureTimeoutSeconds = 10;

	public const int AudioSealBudgetSeconds = 20;

	public const int AudioSealFloorSeconds = 2;
}
