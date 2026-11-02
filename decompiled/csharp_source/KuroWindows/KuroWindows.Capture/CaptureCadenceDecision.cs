using KuroWindows.RecorderCore.Capture;

namespace KuroWindows.Capture;

internal readonly record struct CaptureCadenceDecision(PadCommitKind Kind, bool Reanchored, long LateMs, bool CursorBlendChanged = false)
{
	public bool ShouldCommit => Kind != PadCommitKind.None;

	public static CaptureCadenceDecision NotDue { get; } = new CaptureCadenceDecision(PadCommitKind.None, Reanchored: false, 0L);
}
