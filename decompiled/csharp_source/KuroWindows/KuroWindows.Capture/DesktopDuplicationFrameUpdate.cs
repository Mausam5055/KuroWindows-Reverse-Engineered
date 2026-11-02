namespace KuroWindows.Capture;

internal readonly record struct DesktopDuplicationFrameUpdate(bool HasDesktopUpdate, bool HasPointerUpdate)
{
	public bool IsPointerOnly
	{
		get
		{
			if (!HasDesktopUpdate)
			{
				return HasPointerUpdate;
			}
			return false;
		}
	}
}
