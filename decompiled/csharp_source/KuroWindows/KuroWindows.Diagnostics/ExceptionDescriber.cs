using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using KuroWindows.Auth;

namespace KuroWindows.Diagnostics;

internal static class ExceptionDescriber
{
	public static string Describe(Exception ex)
	{
		if (ex is APIClientException { RetryAfter: var retryAfter } ex2)
		{
			string text;
			if (retryAfter.HasValue)
			{
				TimeSpan valueOrDefault = retryAfter.GetValueOrDefault();
				text = $" retry_after_seconds={(int)Math.Ceiling(valueOrDefault.TotalSeconds)}";
			}
			else
			{
				text = "";
			}
			string value = text;
			return $"type={ex2.GetType().Name} operation={ex2.Operation} status={ex2.StatusCode} code:{ex2.Code ?? "-"} retryable={ex2.IsRetryable}{value} message={Trim(ex2.ServerMessage ?? ex2.Message)}";
		}
		if (ex is HttpRequestException { StatusCode: var statusCode } ex3)
		{
			string text2;
			if (statusCode.HasValue)
			{
				HttpStatusCode valueOrDefault2 = statusCode.GetValueOrDefault();
				int num = (int)valueOrDefault2;
				text2 = num.ToString();
			}
			else
			{
				text2 = "-";
			}
			string value2 = text2;
			return $"type={ex3.GetType().Name} status={value2} message={Trim(ex3.Message)} inner={DescribeInner(ex3.InnerException)}";
		}
		if (!(ex is TaskCanceledException))
		{
			return $"type={ex.GetType().Name} message={Trim(ex.Message)} inner={DescribeInner(ex.InnerException)}";
		}
		return $"type={ex.GetType().Name} likely_timeout_or_cancelled message={Trim(ex.Message)} inner={DescribeInner(ex.InnerException)}";
	}

	private static string DescribeInner(Exception? inner)
	{
		if (inner == null)
		{
			return "-";
		}
		List<string> list = new List<string>();
		Exception ex = inner;
		while (ex != null && list.Count < 3)
		{
			list.Add(ex.GetType().Name + ":" + Trim(ex.Message));
			ex = ex.InnerException;
		}
		return string.Join(" <- ", list);
	}

	private static string Trim(string message)
	{
		string text = message.Replace('\r', ' ').Replace('\n', ' ').Trim();
		if (text.Length > 240)
		{
			return text.Substring(0, 240) + "...";
		}
		return text;
	}
}
