namespace ClientTelemetry;

public enum AppExitReason
{
	Crash,
	NativeCrash,
	Anr,
	InitFailure,
	LowMemory,
	ExcessiveResource,
	Signaled,
	UserRequested,
	UserStopped,
	PackageUpdated,
	PackageStateChange,
	PermissionChange,
	DependencyDied,
	Freezer,
	ExitSelf,
	Other
}
