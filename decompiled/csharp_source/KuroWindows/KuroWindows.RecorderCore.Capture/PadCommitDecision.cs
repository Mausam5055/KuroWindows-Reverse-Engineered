namespace KuroWindows.RecorderCore.Capture;

internal readonly record struct PadCommitDecision(PadCommitKind Kind, bool CursorBlendChanged)
{
	public static PadCommitDecision None { get; } = new PadCommitDecision(PadCommitKind.None, CursorBlendChanged: false);
}
