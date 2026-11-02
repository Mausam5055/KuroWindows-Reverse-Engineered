using System;
using System.IO;

namespace KuroWindows.Upload;

internal static class UploadFileKinds
{
	public static string ContentTypeFor(string filename)
	{
		return Path.GetExtension(filename).ToLowerInvariant() switch
		{
			".mp4" => "video/mp4", 
			".json" => "application/json", 
			".jsonl" => "application/x-ndjson", 
			".txt" => "text/plain", 
			_ => "application/octet-stream", 
		};
	}

	public static int EvidenceRank(string fileName)
	{
		if (fileName.EndsWith(".manifest.json", StringComparison.OrdinalIgnoreCase))
		{
			return 0;
		}
		if (fileName.StartsWith("events_", StringComparison.OrdinalIgnoreCase))
		{
			return 2;
		}
		if (fileName.StartsWith("frame_timestamps_", StringComparison.OrdinalIgnoreCase))
		{
			return 3;
		}
		if (fileName.StartsWith("metadata", StringComparison.OrdinalIgnoreCase))
		{
			return 4;
		}
		return 5;
	}

	public static bool IsVideoFile(string fileName)
	{
		if (!fileName.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
		{
			return fileName.EndsWith(".avi", StringComparison.OrdinalIgnoreCase);
		}
		return true;
	}

	public static bool IsSegmentPayloadFile(string fileName)
	{
		if (!fileName.StartsWith("clip_", StringComparison.OrdinalIgnoreCase) && !fileName.StartsWith("events_", StringComparison.OrdinalIgnoreCase))
		{
			return fileName.StartsWith("frame_timestamps_", StringComparison.OrdinalIgnoreCase);
		}
		return true;
	}
}
