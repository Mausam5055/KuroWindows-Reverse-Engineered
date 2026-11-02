using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using KuroWindows.Diagnostics;

namespace KuroWindows.Input.Uia;

internal sealed class UiaProbeProcessTransport : IUiaProbeTransport, IDisposable
{
	private const string HeapHardLimitBytesHex = "0x4000000";

	private readonly NamedPipeServerStream _pipe;

	private readonly Process _process;

	private int _disposed;

	public int Pid { get; }

	private UiaProbeProcessTransport(NamedPipeServerStream pipe, Process process)
	{
		_pipe = pipe;
		_process = process;
		Pid = process.Id;
	}

	internal static (IUiaProbeTransport? Transport, string Failure) TryStart(int connectTimeoutMs)
	{
		string text = $"{AppConfig.UiaProbePipeNamePrefix}{Guid.NewGuid():N}";
		NamedPipeServerStream namedPipeServerStream = null;
		Process process = null;
		CancellationTokenSource cancellationTokenSource = null;
		try
		{
			namedPipeServerStream = LocalPipes.CreateCurrentUserServer(text, PipeDirection.InOut);
			string text2 = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
			if (string.IsNullOrEmpty(text2))
			{
				return (Transport: null, Failure: "no_exe_path");
			}
			ProcessStartInfo processStartInfo = new ProcessStartInfo(text2);
			processStartInfo.UseShellExecute = false;
			processStartInfo.CreateNoWindow = true;
			processStartInfo.WorkingDirectory = Path.GetDirectoryName(text2) ?? "";
			processStartInfo.ArgumentList.Add("--uia-probe");
			processStartInfo.ArgumentList.Add(text);
			processStartInfo.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
			processStartInfo.Environment["DOTNET_GCHeapHardLimit"] = "0x4000000";
			process = Process.Start(processStartInfo);
			if (process == null)
			{
				return (Transport: null, Failure: "start_returned_null");
			}
			UiaProbeRegistry.RecordSpawn(process.Id);
			Task task = namedPipeServerStream.WaitForConnectionAsync();
			cancellationTokenSource = new CancellationTokenSource();
			Task task2 = process.WaitForExitAsync(cancellationTokenSource.Token);
			int num = Task.WaitAny(new Task[2] { task, task2 }, connectTimeoutMs);
			if (num < 0)
			{
				Observe(task);
				Observe(task2);
				return (Transport: null, Failure: "connect_timeout");
			}
			if (num == 1)
			{
				Observe(task);
				string text3 = TryReadExitCode(process);
				return (Transport: null, Failure: "exited_before_connect:" + text3);
			}
			if (task.IsFaulted)
			{
				Observe(task2);
				return (Transport: null, Failure: "connect_faulted:" + task.Exception?.InnerException?.GetType().Name);
			}
			Observe(task2);
			UiaProbeProcessTransport item = new UiaProbeProcessTransport(namedPipeServerStream, process);
			namedPipeServerStream = null;
			process = null;
			return (Transport: item, Failure: "");
		}
		catch (Exception ex)
		{
			return (Transport: null, Failure: "start_failed:" + ex.GetType().Name);
		}
		finally
		{
			try
			{
				cancellationTokenSource?.Cancel();
				cancellationTokenSource?.Dispose();
			}
			catch
			{
			}
			if (process != null)
			{
				TryKill(process);
				UiaProbeRegistry.RecordExit(process.Id);
				process.Dispose();
			}
			namedPipeServerStream?.Dispose();
		}
	}

	public bool TryExchange(in UiaProbeRequest request, int timeoutMs, out UiaProbeResponse response, out string failure)
	{
		response = default;
		if (timeoutMs <= 0)
		{
			failure = "no_budget";
			return false;
		}
		byte[] payload = UiaProbeProtocol.EncodeRequest(in request);
		CancellationTokenSource cancellationTokenSource = new CancellationTokenSource();
		Task<byte[]> task = ExchangeAsync(payload, cancellationTokenSource.Token);
		if (!TryWait(task, timeoutMs))
		{
			try
			{
				cancellationTokenSource.Cancel();
			}
			catch
			{
			}
			failure = "timeout";
			return false;
		}
		cancellationTokenSource.Dispose();
		if (task.IsFaulted || task.IsCanceled)
		{
			failure = "io:" + (task.Exception?.InnerException?.GetType().Name ?? "canceled");
			return false;
		}
		if (!UiaProbeProtocol.TryDecodeResponse(task.Result, out response))
		{
			failure = "decode";
			return false;
		}
		if (response.Seq != request.Seq)
		{
			failure = "seq_mismatch";
			return false;
		}
		failure = "";
		return true;
	}

	private async Task<byte[]> ExchangeAsync(byte[] payload, CancellationToken cancellationToken)
	{
		await UiaProbeProtocol.WriteFrameAsync(_pipe, payload, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
		return await UiaProbeProtocol.ReadFrameAsync(_pipe, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
	}

	public int? Terminate()
	{
		try
		{
			if (!_process.HasExited)
			{
				_process.Kill(entireProcessTree: true);
			}
			_process.WaitForExit(1000);
			return _process.HasExited ? new int?(_process.ExitCode) : ((int?)null);
		}
		catch
		{
			return null;
		}
	}

	public void Dispose()
	{
		if (Interlocked.Exchange(ref _disposed, 1) == 0)
		{
			try
			{
				_pipe.Dispose();
			}
			catch
			{
			}
			try
			{
				_process.Dispose();
			}
			catch
			{
			}
			UiaProbeRegistry.RecordExit(Pid);
		}
	}

	private static string TryReadExitCode(Process process)
	{
		try
		{
			return process.HasExited ? $"0x{process.ExitCode:x8}" : "-";
		}
		catch
		{
			return "-";
		}
	}

	private static void Observe(Task task)
	{
		if (task.IsCompleted)
		{
			_ = task.Exception;
			return;
		}
		task.ContinueWith((Task t) =>
		{
			_ = t.Exception;
		}, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
	}

	private static void TryKill(Process process)
	{
		try
		{
			if (!process.HasExited)
			{
				process.Kill(entireProcessTree: true);
			}
		}
		catch
		{
		}
	}

	private static bool TryWait(Task task, int timeoutMs)
	{
		bool flag;
		try
		{
			flag = task.Wait(timeoutMs);
		}
		catch (AggregateException)
		{
			return true;
		}
		if (!flag)
		{
			task.ContinueWith((Task t) =>
			{
				_ = t.Exception;
			}, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
		}
		return flag;
	}

	bool IUiaProbeTransport.TryExchange(in UiaProbeRequest request, int timeoutMs, out UiaProbeResponse response, out string failure)
	{
		return TryExchange(in request, timeoutMs, out response, out failure);
	}
}
