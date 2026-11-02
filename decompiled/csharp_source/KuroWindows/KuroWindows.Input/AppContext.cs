namespace KuroWindows.Input;

public sealed record AppContext(string AppName, string BundleId, int DisplayIndex, int Pid, long Hwnd, string WindowClass, string? Aumid)
{
	public static readonly AppContext Unknown = new AppContext("", "", 0, 0, 0L, "", null);
}
