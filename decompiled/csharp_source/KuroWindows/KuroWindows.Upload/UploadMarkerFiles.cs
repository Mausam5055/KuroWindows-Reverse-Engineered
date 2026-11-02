using System;
using System.IO;

namespace KuroWindows.Upload;

internal static class UploadMarkerFiles
{
	private static string LegacyDiagnosticUploadedMarkerPath(string sessionDir, int segmentIndex)
	{
		return Path.Combine(sessionDir, $".upload-diagnostics_{segmentIndex}.txt");
	}

	private static string AttemptCountMarkerPath(string sessionDir, int segmentIndex)
	{
		return Path.Combine(sessionDir, $".upload-attempts_{segmentIndex}.txt");
	}

	private static string OpportunityStampMarkerPath(string sessionDir, int segmentIndex)
	{
		return Path.Combine(sessionDir, $".upload-opportunity_{segmentIndex}.txt");
	}

	private static string HealCountMarkerPath(string sessionDir, int segmentIndex)
	{
		return Path.Combine(sessionDir, $".upload-heals_{segmentIndex}.txt");
	}

	public static void ClearLegacyDiagnosticUploadedMarker(string sessionDir, int segmentIndex)
	{
		Delete(LegacyDiagnosticUploadedMarkerPath(sessionDir, segmentIndex));
	}

	public static int ReadAttemptCount(string sessionDir, int segmentIndex)
	{
		return ReadCount(AttemptCountMarkerPath(sessionDir, segmentIndex));
	}

	public static int IncrementAttemptCount(string sessionDir, int segmentIndex)
	{
		return IncrementCount(AttemptCountMarkerPath(sessionDir, segmentIndex));
	}

	public static void ClearAttemptCount(string sessionDir, int segmentIndex)
	{
		Delete(AttemptCountMarkerPath(sessionDir, segmentIndex));
	}

	public static int ReadHealCount(string sessionDir, int segmentIndex)
	{
		return ReadCount(HealCountMarkerPath(sessionDir, segmentIndex));
	}

	public static int IncrementHealCount(string sessionDir, int segmentIndex)
	{
		return IncrementCount(HealCountMarkerPath(sessionDir, segmentIndex));
	}

	public static void ClearHealCount(string sessionDir, int segmentIndex)
	{
		Delete(HealCountMarkerPath(sessionDir, segmentIndex));
	}

	public static long? ReadOpportunityStamp(string sessionDir, int segmentIndex)
	{
		try
		{
			string path = OpportunityStampMarkerPath(sessionDir, segmentIndex);
			long result;
			return (File.Exists(path) && long.TryParse(File.ReadAllText(path).Trim(), out result) && result >= 0) ? new long?(result) : ((long?)null);
		}
		catch
		{
			return null;
		}
	}

	public static void WriteOpportunityStamp(string sessionDir, int segmentIndex, long seconds)
	{
		try
		{
			File.WriteAllText(OpportunityStampMarkerPath(sessionDir, segmentIndex), seconds.ToString());
		}
		catch
		{
		}
	}

	public static void ClearOpportunityStamp(string sessionDir, int segmentIndex)
	{
		Delete(OpportunityStampMarkerPath(sessionDir, segmentIndex));
	}

	public static void ClearSegmentMarkers(string sessionDir, int segmentIndex)
	{
		Delete(Path.Combine(sessionDir, $".upload-skip_{segmentIndex}.txt"));
		ClearLegacyDiagnosticUploadedMarker(sessionDir, segmentIndex);
		ClearAttemptCount(sessionDir, segmentIndex);
		ClearOpportunityStamp(sessionDir, segmentIndex);
		ClearHealCount(sessionDir, segmentIndex);
	}

	public static bool IsUploadMarkerFile(string fileName)
	{
		if (!fileName.StartsWith(".upload-skip_", StringComparison.OrdinalIgnoreCase) && !fileName.StartsWith(".upload-diagnostics_", StringComparison.OrdinalIgnoreCase) && !fileName.StartsWith(".upload-attempts_", StringComparison.OrdinalIgnoreCase) && !fileName.StartsWith(".upload-opportunity_", StringComparison.OrdinalIgnoreCase))
		{
			return fileName.StartsWith(".upload-heals_", StringComparison.OrdinalIgnoreCase);
		}
		return true;
	}

	private static int ReadCount(string path)
	{
		try
		{
			int result;
			return (File.Exists(path) && int.TryParse(File.ReadAllText(path).Trim(), out result) && result > 0) ? result : 0;
		}
		catch
		{
			return 0;
		}
	}

	private static int IncrementCount(string path)
	{
		int result = ReadCount(path) + 1;
		try
		{
			File.WriteAllText(path, result.ToString());
		}
		catch
		{
		}
		return result;
	}

	private static void Delete(string path)
	{
		try
		{
			if (File.Exists(path))
			{
				File.Delete(path);
			}
		}
		catch
		{
		}
	}
}
