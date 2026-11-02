namespace ClientTelemetry;

public enum SessionEndReason
{
	User,
	Idle,
	Privacy,
	LowDisk,
	Update,
	Shutdown,
	Policy,
	Error,
	Topology,
	CaptureLost,
	Other
}
