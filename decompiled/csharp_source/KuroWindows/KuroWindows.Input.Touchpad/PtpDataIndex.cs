namespace KuroWindows.Input.Touchpad;

internal readonly record struct PtpDataIndex(byte Slot, PtpField Field)
{
	public const byte TopLevel = byte.MaxValue;
}
