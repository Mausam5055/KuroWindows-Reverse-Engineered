using System;

namespace KuroWindows.Upload;

internal sealed class SegmentFateRecordedException(string reason, string errorCode, Exception? cause, bool countsAsFailedUpload) : Exception(reason, cause)
{
	public string ErrorCode { get; } = errorCode;

	public bool CountsAsFailedUpload { get; } = countsAsFailedUpload;
}
