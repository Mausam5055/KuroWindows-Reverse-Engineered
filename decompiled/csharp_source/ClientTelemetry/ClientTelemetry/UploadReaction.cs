namespace ClientTelemetry;

public enum UploadReaction
{
	PresignRefresh,
	DigestRehash,
	ObjectExists,
	UploadGone,
	HealMissingObject,
	HealSizeMismatch,
	HealHashMismatch,
	HealEscalated,
	PartsResumed
}
