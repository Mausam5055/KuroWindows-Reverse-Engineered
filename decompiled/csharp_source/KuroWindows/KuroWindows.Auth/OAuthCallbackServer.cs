using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace KuroWindows.Auth;

public sealed class OAuthCallbackServer : IDisposable
{
	private static readonly TimeSpan PendingCallbackMaxAge = TimeSpan.FromMinutes(5.0);

	private static readonly string PendingCallbackDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppConfig.LocalDataFolderName, "auth");

	private static readonly string PendingCallbackPath = Path.Combine(PendingCallbackDir, "pending-oauth-callback.txt");

	private readonly CancellationTokenSource _cts = new CancellationTokenSource();

	private readonly object _pendingLock = new object();

	private TaskCompletionSource<string>? _pending;

	private string? _lastDeliveredUrl;

	private DateTimeOffset _lastDeliveredAt;

	private int _consecutiveListenFailures;

	public bool IsUrlSchemeRegistered { get; internal set; }

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
				using NamedPipeServerStream server = LocalPipes.CreateCurrentUserServer(AppConfig.OAuthCallbackPipeName, PipeDirection.In);
				await server.WaitForConnectionAsync(ct).ConfigureAwait(continueOnCapturedContext: false);
				if (_consecutiveListenFailures > 1)
				{
					Log($"callback-server: listen loop recovered; suppressed_count={_consecutiveListenFailures - 1}", "ListenLoopAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Auth\\OAuthCallbackServer.cs", 58);
				}
				_consecutiveListenFailures = 0;
				using StreamReader reader = new StreamReader(server, Encoding.UTF8);
				string text = await reader.ReadLineAsync(ct).ConfigureAwait(continueOnCapturedContext: false);
				if (text != null)
				{
					Log("callback-server: received callback over pipe: " + text, "ListenLoopAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Auth\\OAuthCallbackServer.cs", 65);
					CompleteOrPersistCallback(text);
				}
			}
			catch (OperationCanceledException)
			{
				break;
			}
			catch (Exception value)
			{
				if (_consecutiveListenFailures++ == 0)
				{
					Log($"callback-server: listen loop error: {value}", "ListenLoopAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Auth\\OAuthCallbackServer.cs", 77);
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

	public Task<string> WaitForCallbackAsync(CancellationToken ct)
	{
		Log("callback-server: waiting for next callback", "WaitForCallbackAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Auth\\OAuthCallbackServer.cs", 97);
		ClearPendingCallbackFile();
		TaskCompletionSource<string> pending = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
		lock (_pendingLock)
		{
			_pending = pending;
		}
		ct.Register(() =>
		{
			pending.TrySetCanceled(ct);
		});
		PollPendingCallbackFileAsync(pending, DateTimeOffset.UtcNow, ct);
		pending.Task.ContinueWith((Task<string> _) =>
		{
			lock (_pendingLock)
			{
				if (_pending == pending)
				{
					_pending = null;
				}
			}
		}, TaskScheduler.Default);
		return pending.Task;
	}

	public static void ForwardCallbackToRunningInstance(string url)
	{
		PersistPendingCallback(url);
		for (int i = 1; i <= 3; i++)
		{
			try
			{
				Log($"callback-server: forwarding callback (attempt {i}/{3}): {url}", "ForwardCallbackToRunningInstance", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Auth\\OAuthCallbackServer.cs", 137);
				using NamedPipeClientStream namedPipeClientStream = new NamedPipeClientStream(".", AppConfig.OAuthCallbackPipeName, PipeDirection.Out);
				namedPipeClientStream.Connect(10000);
				using StreamWriter streamWriter = new StreamWriter(namedPipeClientStream, Encoding.UTF8)
				{
					AutoFlush = true
				};
				streamWriter.WriteLine(url);
				Log("callback-server: callback forwarded successfully", "ForwardCallbackToRunningInstance", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Auth\\OAuthCallbackServer.cs", 142);
				return;
			}
			catch (Exception ex)
			{
				Log($"callback-server: forward attempt {i}/{3} failed: {ex.Message}", "ForwardCallbackToRunningInstance", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Auth\\OAuthCallbackServer.cs", 147);
				if (i < 3)
				{
					Thread.Sleep(1000);
				}
			}
		}
		Log("callback-server: all forward attempts failed; callback left in pending file", "ForwardCallbackToRunningInstance", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Auth\\OAuthCallbackServer.cs", 153);
	}

	public static bool IsAuthCallbackUrl(string url)
	{
		if (!Uri.TryCreate(url, UriKind.Absolute, out Uri result))
		{
			return false;
		}
		if (string.Equals(result.Scheme, AppConfig.AuthCallbackScheme, StringComparison.OrdinalIgnoreCase) && string.Equals(result.Host, "auth-callback", StringComparison.OrdinalIgnoreCase))
		{
			return !string.IsNullOrWhiteSpace(ExtractCode(url));
		}
		return false;
	}

	internal static string DescribeForLog(string callbackUrl)
	{
		int num = callbackUrl.IndexOf('?');
		string text = ((num < 0) ? callbackUrl : callbackUrl.Substring(0, num));
		if (text.Length > 120)
		{
			return text.Substring(0, 120);
		}
		return text;
	}

	internal static string? ExtractCode(string callbackUrl)
	{
		int num = callbackUrl.IndexOf('?');
		if (num < 0)
		{
			return null;
		}
		string[] array = callbackUrl.Substring(num + 1).Split('&');
		foreach (string text in array)
		{
			int num2 = text.IndexOf('=');
			if (num2 >= 0)
			{
				string text2 = Uri.UnescapeDataString(text.Substring(0, num2));
				string result = Uri.UnescapeDataString(text.Substring(num2 + 1));
				if (text2 == "code")
				{
					return result;
				}
			}
		}
		return null;
	}

	private void CompleteOrPersistCallback(string url)
	{
		if (IsRecentlyDelivered(url))
		{
			Log("callback-server: duplicate callback ignored", "CompleteOrPersistCallback", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Auth\\OAuthCallbackServer.cs", 198);
			return;
		}
		TaskCompletionSource<string> pending;
		lock (_pendingLock)
		{
			pending = _pending;
		}
		if (pending != null)
		{
			if (pending.TrySetResult(url))
			{
				MarkDelivered(url);
				ClearPendingCallbackFile();
			}
			else
			{
				Log("callback-server: callback ignored because wait task already completed", "CompleteOrPersistCallback", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Auth\\OAuthCallbackServer.cs", 217);
			}
		}
		else
		{
			PersistPendingCallback(url);
		}
	}

	private async Task PollPendingCallbackFileAsync(TaskCompletionSource<string> pending, DateTimeOffset waitStartedAt, CancellationToken ct)
	{
		bool readFailureLogged = false;
		while (!ct.IsCancellationRequested && !pending.Task.IsCompleted)
		{
			if (TryReadPendingCallback(waitStartedAt, out string url, ref readFailureLogged))
			{
				if (pending.TrySetResult(url))
				{
					MarkDelivered(url);
					ClearPendingCallbackFile();
				}
				break;
			}
			try
			{
				await Task.Delay(200, ct).ConfigureAwait(continueOnCapturedContext: false);
			}
			catch (OperationCanceledException)
			{
				break;
			}
		}
	}

	private static void PersistPendingCallback(string url)
	{
		try
		{
			Directory.CreateDirectory(PendingCallbackDir);
			string text = Path.Combine(PendingCallbackDir, $"pending-oauth-callback.{Environment.ProcessId}.tmp");
			string contents = $"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}\n{url}";
			File.WriteAllText(text, contents, Encoding.UTF8);
			File.Move(text, PendingCallbackPath, overwrite: true);
			Log("callback-server: callback persisted for running instance", "PersistPendingCallback", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Auth\\OAuthCallbackServer.cs", 266);
		}
		catch (Exception ex)
		{
			Log("callback-server: failed to persist callback: " + ex.Message, "PersistPendingCallback", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Auth\\OAuthCallbackServer.cs", 270);
		}
	}

	private static bool TryReadPendingCallback(DateTimeOffset waitStartedAt, out string url, ref bool readFailureLogged)
	{
		url = "";
		try
		{
			if (!File.Exists(PendingCallbackPath))
			{
				return false;
			}
			string[] array = File.ReadAllLines(PendingCallbackPath, Encoding.UTF8);
			if (array.Length < 2 || !long.TryParse(array[0], out var result) || string.IsNullOrWhiteSpace(array[1]))
			{
				Log("callback-server: pending callback file malformed; discarded", "TryReadPendingCallback", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Auth\\OAuthCallbackServer.cs", 289);
				ClearPendingCallbackFile();
				return false;
			}
			DateTimeOffset dateTimeOffset = DateTimeOffset.FromUnixTimeMilliseconds(result);
			if (dateTimeOffset < waitStartedAt.AddSeconds(-1.0) || DateTimeOffset.UtcNow - dateTimeOffset > PendingCallbackMaxAge)
			{
				Log("callback-server: stale pending callback discarded", "TryReadPendingCallback", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Auth\\OAuthCallbackServer.cs", 298);
				ClearPendingCallbackFile();
				return false;
			}
			url = array[1];
			Log("callback-server: received callback from pending file: " + url, "TryReadPendingCallback", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Auth\\OAuthCallbackServer.cs", 304);
			return true;
		}
		catch (IOException)
		{
			return false;
		}
		catch (Exception ex2)
		{
			if (!readFailureLogged)
			{
				readFailureLogged = true;
				Log("callback-server: failed to read pending callback: " + ex2.Message, "TryReadPendingCallback", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Auth\\OAuthCallbackServer.cs", 316);
			}
			return false;
		}
	}

	private static void ClearPendingCallbackFile()
	{
		try
		{
			if (File.Exists(PendingCallbackPath))
			{
				File.Delete(PendingCallbackPath);
			}
		}
		catch (Exception ex)
		{
			Log("callback-server: failed to clear pending callback: " + ex.Message, "ClearPendingCallbackFile", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Auth\\OAuthCallbackServer.cs", 333);
		}
	}

	private bool IsRecentlyDelivered(string url)
	{
		if (string.Equals(_lastDeliveredUrl, url, StringComparison.Ordinal))
		{
			return DateTimeOffset.UtcNow - _lastDeliveredAt < PendingCallbackMaxAge;
		}
		return false;
	}

	private void MarkDelivered(string url)
	{
		_lastDeliveredUrl = url;
		_lastDeliveredAt = DateTimeOffset.UtcNow;
	}

	public static bool RegisterUrlScheme()
	{
		try
		{
			string text = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule.FileName;
			Log("callback-server: registering protocol handler to " + text, "RegisterUrlScheme", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Auth\\OAuthCallbackServer.cs", 364);
			using RegistryKey registryKey = Registry.CurrentUser.CreateSubKey("Software\\Classes\\" + AppConfig.AuthCallbackScheme);
			registryKey.SetValue("", "URL:" + AppConfig.LocalAppName + " Protocol");
			registryKey.SetValue("URL Protocol", "");
			using RegistryKey registryKey2 = registryKey.CreateSubKey("shell\\open\\command");
			registryKey2.SetValue("", "\"" + text + "\" --auth-callback \"%1\"");
			return true;
		}
		catch (Exception value)
		{
			Log($"callback-server: protocol registration failed: {value}", "RegisterUrlScheme", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Auth\\OAuthCallbackServer.cs", 376);
			return false;
		}
	}

	public void Dispose()
	{
		_cts.Cancel();
		_cts.Dispose();
	}

	private static void Log(string message, [CallerMemberName] string memberName = "", [CallerFilePath] string filePath = "", [CallerLineNumber] int lineNumber = 0)
	{
		KuroLog.Auth(message, memberName, filePath, lineNumber);
	}
}
