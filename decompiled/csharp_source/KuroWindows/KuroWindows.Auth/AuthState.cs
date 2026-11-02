namespace KuroWindows.Auth;

public enum AuthState
{
	SignedOut,
	Restoring,
	SigningIn,
	SignedIn,
	DeviceBanned,
	UserBanned,
	DeviceLinkedToOtherUser
}
