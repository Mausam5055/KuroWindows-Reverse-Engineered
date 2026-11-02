using System.Threading;

namespace KuroWindows.Input;

public static class EventSeq
{
	private static long _seq;

	public static long Next()
	{
		return Interlocked.Increment(ref _seq);
	}
}
