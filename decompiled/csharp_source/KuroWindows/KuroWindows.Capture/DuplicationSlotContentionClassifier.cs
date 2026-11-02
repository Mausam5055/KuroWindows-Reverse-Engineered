using System;
using System.Linq;

namespace KuroWindows.Capture;

internal static class DuplicationSlotContentionClassifier
{
	public const int DxgiNotCurrentlyAvailable = -2005270494;

	public static bool IsSlotContention(Exception ex)
	{
		if (ex.HResult == -2005270494)
		{
			return true;
		}
		if (ex is AggregateException { InnerExceptions: var innerExceptions })
		{
			return innerExceptions.Any(IsSlotContention);
		}
		if (ex.InnerException != null)
		{
			return IsSlotContention(ex.InnerException);
		}
		return false;
	}
}
