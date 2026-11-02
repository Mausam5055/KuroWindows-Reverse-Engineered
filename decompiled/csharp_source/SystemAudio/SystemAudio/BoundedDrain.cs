using System;
using System.Threading.Tasks;

namespace SystemAudio;

internal static class BoundedDrain
{
	public static BoundedDrainOutcome Run(Action body, TimeSpan timeout, out Exception? error)
	{
		error = null;
		Task task = Task.Run(body);
		try
		{
			if (task.Wait(timeout))
			{
				return BoundedDrainOutcome.Ok;
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
			return BoundedDrainOutcome.Error;
		}
		return BoundedDrainOutcome.Wedged;
	}
}
