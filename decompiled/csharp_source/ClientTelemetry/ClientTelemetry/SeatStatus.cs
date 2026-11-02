namespace ClientTelemetry;

public enum SeatStatus
{
	Ok,
	InvalidCode,
	NotFound,
	CodeExpired,
	RecoveryTokenRequired,
	SecretConflict,
	NoHardwareUuid,
	Other
}
