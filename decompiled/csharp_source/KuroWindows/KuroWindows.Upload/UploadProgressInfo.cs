namespace KuroWindows.Upload;

public sealed record UploadProgressInfo(bool IsUploading, int TotalSegments, int CompletedSegments, string? CurrentSessionId, int? CurrentSegmentIndex, string? CurrentFile, long TotalBytes, long UploadedBytes);
