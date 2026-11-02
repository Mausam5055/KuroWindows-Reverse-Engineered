using ClientTelemetry;

namespace KuroWindows.Upload;

internal sealed record UploadFailure(bool RetryInLoop, S3UploadFailure S3, string? HealCode, HealScope? HealScope, string? HealFile, bool UploadGone, bool ServerRejected, ErrorKind ErrorKind, UploadPhase Phase, long? HttpStatus, string ErrorCode, string DurableErrorCode)
{
	public bool NeedsPresign
	{
		get
		{
			S3UploadFailure s = S3;
			if ((uint)(s - 1) <= 1u || s == S3UploadFailure.NoSuchUpload)
			{
				return true;
			}
			return false;
		}
	}

	public bool EscapesLoop
	{
		get
		{
			if (!NeedsPresign && HealCode == null)
			{
				return UploadGone;
			}
			return true;
		}
	}
}
