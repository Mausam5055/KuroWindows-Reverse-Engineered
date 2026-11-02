using System;

namespace KuroWindows.Auth;

internal static class SignupRegion
{
	internal const string Code = "region_unavailable";

	internal static string? Text(string? message)
	{
		if (message == null || !message.Contains("available in your country", StringComparison.OrdinalIgnoreCase))
		{
			return null;
		}
		return message.Trim();
	}

	internal static string? Text(Exception error)
	{
		if (!(error is RegionUnavailableException ex))
		{
			return Text(error.Message);
		}
		return ex.Message;
	}

	internal static string? CallbackText(string callbackUrl)
	{
		if (!Uri.TryCreate(callbackUrl, UriKind.Absolute, out Uri result))
		{
			return null;
		}
		string[] array = result.Query.TrimStart('?').Split('&');
		for (int i = 0; i < array.Length; i++)
		{
			string[] array2 = array[i].Split('=', 2);
			if (array2.Length == 2 && Uri.UnescapeDataString(array2[0]) == "error_description")
			{
				return Text(Uri.UnescapeDataString(array2[1].Replace('+', ' ')));
			}
		}
		return null;
	}
}
