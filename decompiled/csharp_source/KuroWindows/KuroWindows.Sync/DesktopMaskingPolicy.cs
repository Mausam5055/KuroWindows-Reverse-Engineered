using System;
using System.Linq;
using System.Text.Json.Serialization;

namespace KuroWindows.Sync;

public sealed record DesktopMaskingPolicy
{
	[JsonPropertyName("mode")]
	public string Mode { get; init; } = "default";

	[JsonPropertyName("mask_self_app")]
	public bool MaskSelfApp { get; init; } = true;

	[JsonPropertyName("mask_camera")]
	public bool MaskCamera { get; init; } = true;

	[JsonPropertyName("mask_password_fields")]
	public bool MaskPasswordFields { get; init; } = true;

	[JsonPropertyName("privacy_resume_linger_ms")]
	public int PrivacyResumeLingerMs { get; init; } = 2000;

	[JsonPropertyName("blocked_apps")]
	public string[] BlockedApps { get; init; } = Array.Empty<string>();

	[JsonPropertyName("allowed_apps")]
	public string[] AllowedApps { get; init; } = Array.Empty<string>();

	[JsonPropertyName("sensitive_browser_title_keywords")]
	public string[] SensitiveBrowserTitleKeywords { get; init; } = Array.Empty<string>();

	[JsonPropertyName("excluded_browser_title_keywords")]
	public string[] ExcludedBrowserTitleKeywords { get; init; } = Array.Empty<string>();

	[JsonPropertyName("blocked_title_keywords")]
	public string[] LegacyBlockedTitleKeywords { get; init; } = Array.Empty<string>();

	[JsonPropertyName("capture_contention")]
	public string[] CaptureContention { get; init; } = Array.Empty<string>();

	public static volatile DesktopMaskingPolicy Seeded = new DesktopMaskingPolicy();

	public const int CaptureContentionMaxHashes = 64;

	public const int PrivacyResumeLingerFloorMs = 1000;

	public DesktopMaskingPolicy Normalize()
	{
		string[] array = CleanArray(LegacyBlockedTitleKeywords);
		string[] array2 = CleanArray(SensitiveBrowserTitleKeywords);
		return this with
		{
			Mode = (string.IsNullOrWhiteSpace(Mode) ? "default" : Mode.Trim()),
			PrivacyResumeLingerMs = Math.Max(PrivacyResumeLingerMs, 1000),
			BlockedApps = CleanArray(BlockedApps),
			AllowedApps = CleanArray(AllowedApps),
			SensitiveBrowserTitleKeywords = ((array2.Length != 0) ? array2 : array),
			ExcludedBrowserTitleKeywords = CleanArray(ExcludedBrowserTitleKeywords),
			LegacyBlockedTitleKeywords = array,
			CaptureContention = CleanDigests(CaptureContention)
		};
	}

	public bool TryValidate(out string error)
	{
		if (string.IsNullOrWhiteSpace(Mode))
		{
			error = "masking.mode is required";
			return false;
		}
		if (BlockedApps == null || AllowedApps == null || SensitiveBrowserTitleKeywords == null || ExcludedBrowserTitleKeywords == null || LegacyBlockedTitleKeywords == null || CaptureContention == null)
		{
			error = "masking app, browser title and capture contention lists must be arrays";
			return false;
		}
		int privacyResumeLingerMs = PrivacyResumeLingerMs;
		if ((privacyResumeLingerMs < 0 || privacyResumeLingerMs > 10000) ? true : false)
		{
			error = "masking.privacy_resume_linger_ms must be between 0 and 10000";
			return false;
		}
		error = "";
		return true;
	}

	private static string[] CleanArray(string[]? values)
	{
		return values?.Select((string v) => v.Trim()).Where((string v) => v.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)
			.ToArray() ?? Array.Empty<string>();
	}

	private static string[] CleanDigests(string[]? values)
	{
		return values?.Select((string v) => v?.Trim().ToLowerInvariant() ?? "").Where(IsSha256Hex).Distinct(StringComparer.Ordinal)
			.Take(64)
			.ToArray() ?? Array.Empty<string>();
	}

	private static bool IsSha256Hex(string value)
	{
		if (value.Length != 64)
		{
			return false;
		}
		for (int i = 0; i < value.Length; i++)
		{
			bool flag;
			switch (value[i])
			{
			case '0':
			case '1':
			case '2':
			case '3':
			case '4':
			case '5':
			case '6':
			case '7':
			case '8':
			case '9':
			case 'a':
			case 'b':
			case 'c':
			case 'd':
			case 'e':
			case 'f':
				flag = true;
				break;
			default:
				flag = false;
				break;
			}
			if (!flag)
			{
				return false;
			}
		}
		return true;
	}
}
