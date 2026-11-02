namespace ClientTelemetry;

public enum ErrorKind
{
	Network,
	Timeout,
	Http4xx,
	Http5xx,
	Tls,
	Disk,
	Auth,
	Blocked,
	UpdateRequired,
	Cancelled,
	PresignRejected,
	DigestMismatch,
	UploadGone,
	IntegrityHeal,
	Unknown
}
