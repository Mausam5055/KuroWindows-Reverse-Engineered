namespace KuroWindows.Input;

public enum RowKind : byte
{
	MouseMove,
	Drag,
	Click,
	Scroll,
	ScrollOs,
	Key,
	ModifierChange,
	ScreenTransition,
	MouseDelta,
	Touch,
	RawDelta,
	InjectedMove,
	Cold,
	Addressed,
	FrameZero,
	SegmentEnd,
	Barrier,
	Boundary
}
