using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace DeviceIdentity;

internal sealed class OncePerCodeGate
{
	private readonly ConcurrentDictionary<string, Lazy<Task<bool>>> _runs = new ConcurrentDictionary<string, Lazy<Task<bool>>>();

	public async Task<bool> RunAsync(string code, Func<Task<bool>> work, CancellationToken cancellationToken = default(CancellationToken))
	{
		Lazy<Task<bool>> lazy = new Lazy<Task<bool>>(async () => await work().ConfigureAwait(continueOnCapturedContext: false), LazyThreadSafetyMode.ExecutionAndPublication);
		while (true)
		{
			Lazy<Task<bool>> orAdd = _runs.GetOrAdd(code, lazy);
			if (orAdd == lazy || !orAdd.Value.IsCompleted)
			{
				return await orAdd.Value.WaitAsync(cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
			}
			if (orAdd.Value.IsCompletedSuccessfully && orAdd.Value.Result)
			{
				break;
			}
			_runs.TryUpdate(code, lazy, orAdd);
		}
		return false;
	}

	public bool HasRun(string code)
	{
		return _runs.ContainsKey(code);
	}
}
