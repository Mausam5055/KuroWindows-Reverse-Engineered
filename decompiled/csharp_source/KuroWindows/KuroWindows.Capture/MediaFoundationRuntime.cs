using Vortice.MediaFoundation;

namespace KuroWindows.Capture;

internal static class MediaFoundationRuntime
{
	private static readonly object LockObj = new object();

	private static int _refCount;

	public static void Acquire()
	{
		lock (LockObj)
		{
			if (_refCount == 0)
			{
				MediaFactory.MFStartup();
			}
			_refCount++;
		}
	}

	public static void Release()
	{
		lock (LockObj)
		{
			if (_refCount != 0)
			{
				_refCount--;
				if (_refCount == 0)
				{
					MediaFactory.MFShutdown();
				}
			}
		}
	}
}
