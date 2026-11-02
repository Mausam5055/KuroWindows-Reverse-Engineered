namespace KuroWindows.Input;

public readonly struct EventRow
{
	public RowKind Kind { get; init; }

	public RowFlags Flags { get; init; }

	public long WallMs { get; init; }

	public uint OsTime { get; init; }

	public int SourcePid { get; init; }

	public ulong ExtraInfo { get; init; }

	public AppContext? App { get; init; }

	public AppContext? Target { get; init; }

	public object? Ref { get; init; }

	public double X { get; init; }

	public double Y { get; init; }

	public int Screen { get; init; }

	public int Display { get; init; }

	public MouseButton Button { get; init; }

	public int ClickCount { get; init; }

	public int FromScreen { get; init; }

	public int ToScreen { get; init; }

	public uint Modifiers { get; init; }

	public double DeltaX { get; init; }

	public double DeltaY { get; init; }

	public int Phase { get; init; }

	public ScrollChannel Channel { get; init; }

	public int GestureId { get; init; }

	public double VelocityX { get; init; }

	public double VelocityY { get; init; }

	public double TotalMmX { get; init; }

	public double TotalMmY { get; init; }

	public ScrollEndReason EndReason { get; init; }

	public uint VkCode { get; init; }

	public uint ScanCode { get; init; }

	public long Hkl { get; init; }

	public string? Chars { get; init; }

	public int Dx { get; init; }

	public int Dy { get; init; }

	public int Packets { get; init; }

	public int Device { get; init; }

	public int ScanTime { get; init; }

	public int ContactCount { get; init; }

	public int Buttons { get; init; }

	public int Frames { get; init; }

	public int ContactsCarried { get; init; }

	public TouchContacts Contacts { get; init; }
}
