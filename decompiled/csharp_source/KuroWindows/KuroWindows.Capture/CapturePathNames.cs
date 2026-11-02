namespace KuroWindows.Capture;

internal static class CapturePathNames
{
	public static string Name(CapturePathKind path)
	{
		return path switch
		{
			CapturePathKind.DxgiDda => "dxgi_dda", 
			CapturePathKind.WgcBorderless => "wgc_borderless", 
			CapturePathKind.WgcBordered => "wgc_bordered", 
			_ => "unknown", 
		};
	}
}
