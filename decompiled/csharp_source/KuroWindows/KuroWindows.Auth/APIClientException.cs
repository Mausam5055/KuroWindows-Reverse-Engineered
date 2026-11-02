using System;

namespace KuroWindows.Auth;

public sealed class APIClientException(string operation, int statusCode, string? responseBody, string? code = null, string? serverMessage = null, TimeSpan? retryAfter = null) : Exception($"{operation} failed {statusCode}: {responseBody}")
{
	public string Operation { get; } = operation;

	public int StatusCode { get; } = statusCode;

	public string? ResponseBody { get; } = responseBody;

	public string? Code { get; } = code;

	public string? ServerMessage { get; } = serverMessage;

	public TimeSpan? RetryAfter { get; } = retryAfter;

	internal bool ProofSigned { get; set; }

	public bool IsSessionExpired
	{
		get
		{
			bool flag = StatusCode == 401;
			if (!flag)
			{
				string code = Code;
				bool flag2 = ((code == "unauthorized" || code == "session_expired") ? true : false);
				flag = flag2;
			}
			if (!flag)
			{
				return ContainsToken("session_expired");
			}
			return true;
		}
	}

	public bool IsUpdateRequired
	{
		get
		{
			if (StatusCode != 426)
			{
				return Code == "update_required";
			}
			return true;
		}
	}

	public bool IsDeviceOrProfileBlocked
	{
		get
		{
			int statusCode = StatusCode;
			bool flag = (uint)(statusCode - 403) <= 1u;
			bool flag2 = flag;
			if (flag2)
			{
				bool flag3;
				switch (Code)
				{
				case "device_banned":
				case "device_access_denied":
				case "profile_not_found":
				case "device_not_found":
					flag3 = true;
					break;
				default:
					flag3 = false;
					break;
				}
				flag2 = flag3;
			}
			return flag2;
		}
	}

	public bool IsRetryable
	{
		get
		{
			if (StatusCode != 408 && !(Code == "timeout") && StatusCode != 429 && StatusCode != 503)
			{
				return StatusCode >= 500;
			}
			return true;
		}
	}

	private bool ContainsToken(string token)
	{
		if (!string.IsNullOrEmpty(ResponseBody))
		{
			return ResponseBody.Contains(token, StringComparison.OrdinalIgnoreCase);
		}
		return false;
	}
}
