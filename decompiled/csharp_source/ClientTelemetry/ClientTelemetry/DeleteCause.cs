namespace ClientTelemetry;

public enum DeleteCause
{
	BacklogPurged,
	QuarantineExpired,
	CorruptManifestExpired,
	OrphanExpired,
	DebrisExpired,
	AlreadyUploadedCollision
}
