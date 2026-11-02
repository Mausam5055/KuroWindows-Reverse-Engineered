using System;

namespace DesktopUpdates;

internal sealed class UpdateDownloadStalledException : TimeoutException
{
	public UpdateDownloadStalledException(string message)
		: base(message)
	{
	}
}
