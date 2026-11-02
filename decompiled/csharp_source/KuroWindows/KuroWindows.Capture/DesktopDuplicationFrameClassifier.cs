namespace KuroWindows.Capture;

internal static class DesktopDuplicationFrameClassifier
{
	public static DesktopDuplicationFrameUpdate Classify(long lastPresentTime, long accumulatedFrames, long lastMouseUpdateTime)
	{
		return new DesktopDuplicationFrameUpdate(accumulatedFrames > 0 || lastPresentTime != 0, lastMouseUpdateTime != 0);
	}
}
