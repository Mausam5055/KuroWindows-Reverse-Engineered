namespace ClientTelemetry;

public enum QuarantineReason
{
	EncodeFailed,
	ZeroFrames,
	FileMissing,
	ContainerInvalid,
	FinalizeTimeout,
	ManifestWriteFailed,
	NoDisplays,
	DurationZero,
	SidecarInvalid,
	TimestampUnresolved,
	RecoveryFailed,
	HealEscalated,
	Legacy
}
