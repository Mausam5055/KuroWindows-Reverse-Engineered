using System.Threading;

namespace KuroWindows.RecorderCore.Encode;

internal static class LeakedWriterCounter
{
	private static long _count;

	public static long Count => Interlocked.Read(in _count);

	public static void Increment()
	{
		Interlocked.Increment(ref _count);
	}
}
