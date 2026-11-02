using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ClientTelemetry;

namespace KuroWindows;

internal sealed class AppCommandServer : IDisposable
{
	public const string ShowSettingsCommand = "show-settings";

	public const string QuitCommand = "quit";

	private readonly CancellationTokenSource _cts = new CancellationTokenSource();

	private readonly Action<string> _handleCommand;

	private bool _listenErrorLogged;

	private long _suppressedListenErrors;

	public AppCommandServer(Action<string> handleCommand)
	{
		_handleCommand = handleCommand;
	}

	public void Start()
	{
		ListenLoopAsync(_cts.Token);
	}

	private async Task ListenLoopAsync(CancellationToken ct)
	{
		while (!ct.IsCancellationRequested)
		{
			try
			{
				using NamedPipeServerStream server = LocalPipes.CreateCurrentUserServer(AppConfig.AppCommandPipeName, PipeDirection.InOut);
				await server.WaitForConnectionAsync(ct).ConfigureAwait(continueOnCapturedContext: false);
				if (_listenErrorLogged)
				{
					KuroLog.App($"App command server accepting again suppressed_listen_errors={_suppressedListenErrors}", "ListenLoopAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\AppCommandServer.cs", 37);
					_listenErrorLogged = false;
					_suppressedListenErrors = 0L;
				}
				using StreamReader reader = new StreamReader(server, Encoding.UTF8);
				string command = await reader.ReadLineAsync(ct).ConfigureAwait(continueOnCapturedContext: false);
				if (!string.IsNullOrWhiteSpace(command))
				{
					using StreamWriter writer = new StreamWriter(server, Encoding.UTF8)
					{
						AutoFlush = true
					};
					await writer.WriteLineAsync("ok").ConfigureAwait(continueOnCapturedContext: false);
					KuroLog.LocalOnly("app", "App command received: " + command, "ListenLoopAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\AppCommandServer.cs", 48);
					_handleCommand(command);
				}
			}
			catch (OperationCanceledException)
			{
				break;
			}
			catch (Exception value)
			{
				if (!_listenErrorLogged)
				{
					_listenErrorLogged = true;
					KuroLog.App($"App command server error: {value}", "ListenLoopAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\AppCommandServer.cs", 63);
				}
				else
				{
					_suppressedListenErrors++;
				}
				try
				{
					await Task.Delay(TimeSpan.FromSeconds(1.0), ct).ConfigureAwait(continueOnCapturedContext: false);
				}
				catch (OperationCanceledException)
				{
					break;
				}
			}
		}
	}

	public static void SendCommandToRunningInstance(string command)
	{
		for (int i = 1; i <= 8; i++)
		{
			try
			{
				using NamedPipeClientStream namedPipeClientStream = new NamedPipeClientStream(".", AppConfig.AppCommandPipeName, PipeDirection.InOut);
				namedPipeClientStream.Connect(1000);
				using StreamWriter streamWriter = new StreamWriter(namedPipeClientStream, Encoding.UTF8)
				{
					AutoFlush = true
				};
				using StreamReader streamReader = new StreamReader(namedPipeClientStream, Encoding.UTF8);
				streamWriter.WriteLine(command);
				Task<string?> task = streamReader.ReadLineAsync();
				if (!task.Wait(TimeSpan.FromMilliseconds(2000.0)))
				{
					throw new TimeoutException("Timed out waiting for app command acknowledgement.");
				}
				string result = task.Result;
				if (string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
				{
					KuroLog.LocalOnly("app", "App command forwarded: " + command, "SendCommandToRunningInstance", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\AppCommandServer.cs", 107);
					return;
				}
				throw new IOException("Unexpected app command acknowledgement: " + (result ?? "<none>"));
			}
			catch (Exception ex)
			{
				KuroLog.LocalOnly("app", $"App command forward attempt {i}/{8} failed: {ex.Message}", "SendCommandToRunningInstance", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\AppCommandServer.cs", 115);
				if (i < 8)
				{
					Thread.Sleep(250);
				}
			}
		}
		KuroLog.App($"App command forward failed after {8} attempts: {command}", "SendCommandToRunningInstance", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\AppCommandServer.cs", 123);
		Telemetry.Emit(Ev.UiCommandForwardFailed(8L, command));
		Telemetry.FlushToDisk();
	}

	public void Dispose()
	{
		_cts.Cancel();
		_cts.Dispose();
	}
}
