using System;

namespace KuroWindows.Upload;

internal sealed class UploadDigestRejectedException(string fileName, S3UploadException cause) : Exception("S3 rejected the digest of " + fileName + " and a re-hash left it unchanged: " + cause.Message, cause)
{
	public string FileName { get; } = fileName;

	public S3UploadException Cause { get; } = cause;
}
