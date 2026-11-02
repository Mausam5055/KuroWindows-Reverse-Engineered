using System;
using System.Threading.Tasks;
using Vortice.MediaFoundation;

namespace KuroWindows.RecorderCore.Encode;

internal static class BoundedFinalize
{
	public static BoundedFinalizeOutcome Drain(IMFSinkWriter writer, int streamIndex, TimeSpan timeout, out Exception? error)
	{
		return Drain(() =>
		{
			writer.NotifyEndOfSegment(streamIndex);
			writer.Finalize();
		}, timeout, out error);
	}

	internal static BoundedFinalizeOutcome Drain(Action drainBody, TimeSpan timeout, out Exception? error)
	{
		error = null;
		Task task = Task.Run(drainBody);
		try
		{
			if (task.Wait(timeout))
			{
				return BoundedFinalizeOutcome.Ok;
			}
		}
		catch (Exception ex)
		{
			Exception ex2;
			if (ex is AggregateException)
			{
				Exception innerException = ex.InnerException;
				if (innerException != null)
				{
					ex2 = innerException;
					goto IL_0031;
				}
			}
			ex2 = ex;
			goto IL_0031;
			IL_0031:
			error = ex2;
			return BoundedFinalizeOutcome.Error;
		}
		return BoundedFinalizeOutcome.Wedged;
	}
}
