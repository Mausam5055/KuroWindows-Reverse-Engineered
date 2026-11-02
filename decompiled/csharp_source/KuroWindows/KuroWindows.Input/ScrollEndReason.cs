namespace KuroWindows.Input;

public enum ScrollEndReason : byte
{
	None,
	Lift,
	Button,
	Pinch,
	Fingers,
	Timeout,
	DeviceRemoved,
	SegmentClose
}
