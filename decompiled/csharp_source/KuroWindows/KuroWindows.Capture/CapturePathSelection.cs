namespace KuroWindows.Capture;

internal readonly record struct CapturePathSelection(CapturePathKind Path, bool CaptureBorderVisible, string? CaptureBorderBlockerReason, string SelectionReason)
{
	public string PathName => CapturePathNames.Name(Path);

	public static CapturePathSelection Dxgi(string reason = "primary")
	{
		return new CapturePathSelection(CapturePathKind.DxgiDda, CaptureBorderVisible: false, null, reason);
	}

	public static CapturePathSelection Wgc(WgcBorderMode borderMode, string? blockerReason, string reason)
	{
		return new CapturePathSelection((borderMode != WgcBorderMode.AllowVisibleBorder) ? CapturePathKind.WgcBorderless : CapturePathKind.WgcBordered, borderMode == WgcBorderMode.AllowVisibleBorder, (borderMode == WgcBorderMode.AllowVisibleBorder) ? blockerReason : null, reason);
	}
}
