using System;
using System.Text.RegularExpressions;
using KuroWindows.Sync;

namespace KuroWindows.Upload;

public static class UploadCompletionRegion
{
	public const string Default = "us-east-1";

	private const string Auto = "auto";

	private static readonly Regex RegionPattern = new Regex("^[a-z]{2}-[a-z]+-[0-9]$", RegexOptions.Compiled);

	private static volatile string? _served;

	private static volatile string _lastAppliedDescription = "absent";

	private static string Effective => _served ?? "us-east-1";

	public static string? HeaderValue
	{
		get
		{
			if (!(Effective == "auto"))
			{
				return Effective;
			}
			return null;
		}
	}

	public static void Apply(DesktopUploadPolicy? upload)
	{
		string text = null;
		string text2 = upload?.CompletionRegion?.Trim().ToLowerInvariant();
		if (text2 != null && (text2 == "auto" || RegionPattern.IsMatch(text2)))
		{
			text = text2;
		}
		_served = text;
		string text3 = ((text == null) ? "absent" : ("region=" + text));
		if (!string.Equals(_lastAppliedDescription, text3, StringComparison.Ordinal))
		{
			_lastAppliedDescription = text3;
			KuroLog.Upload($"completion region policy applied {text3} (default {"us-east-1"}; auto sends no header)", "Apply", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Upload\\UploadCompletionRegion.cs", 49);
		}
	}

	internal static void ResetForTest()
	{
		_served = null;
		_lastAppliedDescription = "absent";
	}
}
