namespace KuroWindows.Upload;

public enum S3UploadFailure
{
	Other,
	PresignRejected,
	DigestMismatch,
	ObjectExists,
	NoSuchUpload,
	PayloadTooLarge
}
