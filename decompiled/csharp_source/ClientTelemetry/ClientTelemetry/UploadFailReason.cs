namespace ClientTelemetry;

public enum UploadFailReason
{
	Network,
	Http4xx,
	Http5xx,
	Disk,
	PresignRejected,
	DigestMismatch,
	UploadGone,
	IntegrityHeal,
	Other
}
