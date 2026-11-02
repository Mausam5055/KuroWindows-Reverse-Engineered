namespace KuroWindows.Input.Touchpad;

internal interface IPtpGestureSink
{
	void ScrollBegan(int gestureId, double dxPx, double dyPx, long t);

	void ScrollChanged(int gestureId, double dxPx, double dyPx, long t);

	void ScrollEnded(int gestureId, double velocityX, double velocityY, double totalMmX, double totalMmY, ScrollEndReason reason, long t);

	void GestureRow(string kind, int fingers, double? scale, double dxMm, double dyMm, long durationMs, int gestureId, long t);

	void GestureWithoutRow();
}
