using System.Text.Json.Serialization;
using KuroWindows.Upload;

namespace KuroWindows.CoreBridge;

public sealed record CoreBridgeUploadState
{
	[JsonPropertyName("is_uploading")]
	public bool IsUploading { get; init; }

	[JsonPropertyName("total_segments")]
	public int TotalSegments { get; init; }

	[JsonPropertyName("completed_segments")]
	public int CompletedSegments { get; init; }

	[JsonPropertyName("current_session_id")]
	public string? CurrentSessionId { get; init; }

	[JsonPropertyName("current_segment_index")]
	public int? CurrentSegmentIndex { get; init; }

	[JsonPropertyName("current_file")]
	public string? CurrentFile { get; init; }

	[JsonPropertyName("total_bytes")]
	public long TotalBytes { get; init; }

	[JsonPropertyName("uploaded_bytes")]
	public long UploadedBytes { get; init; }

	public static CoreBridgeUploadState FromProgress(UploadProgressInfo? progress)
	{
		if (!(progress == null))
		{
			return new CoreBridgeUploadState
			{
				IsUploading = progress.IsUploading,
				TotalSegments = progress.TotalSegments,
				CompletedSegments = progress.CompletedSegments,
				CurrentSessionId = progress.CurrentSessionId,
				CurrentSegmentIndex = progress.CurrentSegmentIndex,
				CurrentFile = progress.CurrentFile,
				TotalBytes = progress.TotalBytes,
				UploadedBytes = progress.UploadedBytes
			};
		}
		return new CoreBridgeUploadState();
	}
}
