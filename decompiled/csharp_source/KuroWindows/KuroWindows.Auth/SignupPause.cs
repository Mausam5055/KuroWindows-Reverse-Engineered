using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace KuroWindows.Auth;

internal static class SignupPause
{
	internal const string Code = "signup_disabled";

	internal const string Message = "Sign-ups are currently paused.";

	internal static bool Matches(Exception error)
	{
		if (!(error is SignupPausedException))
		{
			return Matches(null, error.Message);
		}
		return true;
	}

	internal static bool Matches(string? code = null, string? message = null)
	{
		if ((!(code == "signup_disabled") && !(code == "signups_disabled")) || 1 == 0)
		{
			if (message != null)
			{
				return Regex.IsMatch(message, "sign[ -]?ups? (?:are )?(?:currently )?paused|signups? not allowed", RegexOptions.IgnoreCase);
			}
			return false;
		}
		return true;
	}

	internal static bool MatchesCallback(string callbackUrl)
	{
		if (!Uri.TryCreate(callbackUrl, UriKind.Absolute, out Uri result))
		{
			return false;
		}
		Dictionary<string, string> dictionary = new Dictionary<string, string>();
		string[] array = result.Query.TrimStart('?').Split('&');
		for (int i = 0; i < array.Length; i++)
		{
			string[] array2 = array[i].Split('=', 2);
			if (array2.Length == 2)
			{
				dictionary[Uri.UnescapeDataString(array2[0])] = Uri.UnescapeDataString(array2[1].Replace('+', ' '));
			}
		}
		return Matches(dictionary.GetValueOrDefault("error_code") ?? dictionary.GetValueOrDefault("error"), dictionary.GetValueOrDefault("error_description"));
	}
}
