using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace KuroWindows.Capture;

internal static class CaptureAccessDeniedClassifier
{
	public const int ComAccessDenied = -2147024891;

	public const int DxgiAccessDenied = -2005270485;

	public static bool IsAccessDenied(Exception ex)
	{
		if (ex is CaptureAccessDeniedException)
		{
			return true;
		}
		if (IsAccessDeniedHResult(ex.HResult))
		{
			return true;
		}
		if (ex is COMException ex2 && IsAccessDeniedHResult(ex2.HResult))
		{
			return true;
		}
		if (ex is AggregateException { InnerExceptions: var innerExceptions })
		{
			return innerExceptions.Any(IsAccessDenied);
		}
		if (ex.InnerException != null)
		{
			return IsAccessDenied(ex.InnerException);
		}
		return false;
	}

	public static bool IsAccessDeniedHResult(int hresult)
	{
		if (hresult == -2147024891 || hresult == -2005270485)
		{
			return true;
		}
		return false;
	}

	public static int LoggableHResult(Exception ex)
	{
		return FirstDenied(ex) ?? ex.HResult;
		static int? FirstDenied(Exception ex2)
		{
			if (IsAccessDeniedHResult(ex2.HResult))
			{
				return ex2.HResult;
			}
			if (ex2 is AggregateException ex3)
			{
				foreach (Exception innerException in ex3.InnerExceptions)
				{
					int? num = FirstDenied(innerException);
					if (num.HasValue)
					{
						return num.GetValueOrDefault();
					}
				}
			}
			if (ex2.InnerException == null)
			{
				return null;
			}
			return FirstDenied(ex2.InnerException);
		}
	}

	public static string InnerHResultsLogValue(Exception ex)
	{
		List<int> collected = new List<int>(4);
		Collect(ex);
		return string.Join(",", collected.Select((int hr) => $"0x{hr:X8}"));
		void Collect(Exception current)
		{
			if (collected.Count < 4)
			{
				if (!collected.Contains(current.HResult))
				{
					collected.Add(current.HResult);
					if (collected.Count >= 4)
					{
						return;
					}
				}
				if (current is AggregateException ex2)
				{
					{
						foreach (Exception innerException in ex2.InnerExceptions)
						{
							Collect(innerException);
							if (collected.Count >= 4)
							{
								break;
							}
						}
						return;
					}
				}
				if (current.InnerException != null)
				{
					Collect(current.InnerException);
				}
			}
		}
	}
}
