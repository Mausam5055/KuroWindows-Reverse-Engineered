using System.Drawing;
using KuroWindows.RecorderCore.Capture;

namespace KuroWindows.Capture;

internal readonly record struct CursorFrameSample(bool HasCursor, Point Hotspot, bool Blended, string Source, bool Visible = false, bool Suppressed = false, string Shape = "hidden")
{
	public static CursorFrameSample None => new CursorFrameSample(HasCursor: false, Point.Empty, Blended: false, "none");

	public static CursorFrameSample FromSystemCursor(Point hotspot, bool blended, in PadCursorObservation cursor)
	{
		return new CursorFrameSample(HasCursor: true, hotspot, blended, "system-cursor", cursor.Visible, cursor.Suppressed, CursorShapes.Classify(cursor.RawSprite, cursor.Visible));
	}

	public static CursorFrameSample FromDuplicatedPointer(Point hotspot, in PadCursorObservation cursor)
	{
		return new CursorFrameSample(HasCursor: true, hotspot, Blended: true, "dxgi-duplicated-pointer", Visible: true, cursor.Suppressed, CursorShapes.Classify(cursor.RawSprite, visible: true));
	}
}
