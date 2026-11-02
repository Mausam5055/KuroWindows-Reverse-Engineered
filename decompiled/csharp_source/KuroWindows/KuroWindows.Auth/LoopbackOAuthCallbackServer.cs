using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace KuroWindows.Auth;

internal sealed class LoopbackOAuthCallbackServer : IDisposable
{
	private static readonly TimeSpan PerConnectionTimeout = TimeSpan.FromSeconds(10.0);

	private readonly Uri _redirectUri;

	private TcpListener? _listener;

	private bool _rejectionLogged;

	private bool _clientFaultLogged;

	private const string SuccessHtml = "<!doctype html>\r\n<html lang=\"en\">\r\n<head>\r\n  <meta charset=\"utf-8\">\r\n  <meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\r\n  <title>Kuro Sign-In Complete</title>\r\n  <style>\r\n    body {\r\n      margin: 0;\r\n      min-height: 100vh;\r\n      display: grid;\r\n      place-items: center;\r\n      font-family: -apple-system, BlinkMacSystemFont, \"Segoe UI\", sans-serif;\r\n      background: #f7f7f7;\r\n      color: #050505;\r\n    }\r\n    main {\r\n      width: min(420px, calc(100vw - 32px));\r\n      padding: 32px 28px;\r\n      border: 1px solid #e4e4e4;\r\n      border-radius: 12px;\r\n      background: white;\r\n      text-align: center;\r\n      box-shadow: 0 10px 30px rgba(0,0,0,.08);\r\n    }\r\n    h1 { margin: 0 0 12px; font-size: 24px; }\r\n    p { margin: 0 0 6px; color: #666; line-height: 1.5; }\r\n    .hint { margin-top: 14px; font-size: 13px; color: #999; }\r\n  </style>\r\n</head>\r\n<body>\r\n  <main>\r\n    <h1>Signed in!</h1>\r\n    <p>You can close this tab.</p>\r\n    <p class=\"hint\">Switch back to the Kuro app on your taskbar to finish onboarding.</p>\r\n  </main>\r\n</body>\r\n</html>";

	public string RedirectUrl => _redirectUri.ToString();

	public LoopbackOAuthCallbackServer(string redirectUrl)
	{
		_redirectUri = new Uri(redirectUrl);
		if (!string.Equals(_redirectUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) || !IPAddress.TryParse(_redirectUri.Host, out IPAddress address) || !IPAddress.IsLoopback(address) || _redirectUri.Port <= 0)
		{
			throw new ArgumentException("Loopback redirect URL must be an explicit http loopback URL: " + redirectUrl, "redirectUrl");
		}
	}

	public void Start()
	{
		if (_listener != null)
		{
			throw new InvalidOperationException("Loopback OAuth callback server is already started.");
		}
		_listener = new TcpListener(IPAddress.Loopback, _redirectUri.Port);
		_listener.Start();
		Log("loopback-callback: listening on " + RedirectUrl, "Start", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Auth\\LoopbackOAuthCallbackServer.cs", 45);
	}

	public async Task<string> WaitForCallbackAsync(CancellationToken ct)
	{
		TcpListener listener = _listener ?? throw new InvalidOperationException("Loopback callback server is not started.");
		using (ct.Register(StopListener))
		{
			while (!ct.IsCancellationRequested)
			{
				TcpClient tcpClient;
				try
				{
					tcpClient = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(continueOnCapturedContext: false);
				}
				catch (OperationCanceledException)
				{
					throw;
				}
				catch (ObjectDisposedException) when (ct.IsCancellationRequested)
				{
					throw new OperationCanceledException(ct);
				}
				catch (SocketException) when (ct.IsCancellationRequested)
				{
					throw new OperationCanceledException(ct);
				}
				using (tcpClient)
				{
					string callbackUrl = null;
					try
					{
						using CancellationTokenSource connectionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
						connectionCts.CancelAfter(PerConnectionTimeout);
						callbackUrl = await HandleClientAsync(tcpClient, connectionCts.Token).ConfigureAwait(continueOnCapturedContext: false);
					}
					catch (OperationCanceledException) when (!ct.IsCancellationRequested)
					{
						LogClientFaultOnce("connection-deadline");
					}
					catch (Exception ex5) when ((ex5 is IOException || ex5 is SocketException) ? true : false)
					{
						LogClientFaultOnce(ex5.GetType().Name);
					}
					if (callbackUrl != null)
					{
						return callbackUrl;
					}
				}
			}
			throw new OperationCanceledException(ct);
		}
	}

	private void LogClientFaultOnce(string kind)
	{
		if (!_clientFaultLogged)
		{
			_clientFaultLogged = true;
			Log("loopback-callback: client connection fault (" + kind + "); continuing to accept", "LogClientFaultOnce", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Auth\\LoopbackOAuthCallbackServer.cs", 102);
		}
	}

	private async Task<string?> HandleClientAsync(TcpClient client, CancellationToken ct)
	{
		string result;
		await using (NetworkStream stream = client.GetStream())
		{
			using StreamReader reader = new StreamReader(stream, Encoding.ASCII, detectEncodingFromByteOrderMarks: true, -1, leaveOpen: true);
			string requestLine = await reader.ReadLineAsync(ct).ConfigureAwait(continueOnCapturedContext: false);
			if (string.IsNullOrWhiteSpace(requestLine))
			{
				if (requestLine != null)
				{
					LogRejectionOnce(HttpStatusCode.BadRequest, "-");
				}
				await WriteResponseAsync(stream, HttpStatusCode.BadRequest, "Bad Request", ct).ConfigureAwait(continueOnCapturedContext: false);
				result = null;
			}
			else
			{
				while (!string.IsNullOrEmpty(await reader.ReadLineAsync(ct).ConfigureAwait(continueOnCapturedContext: false)))
				{
				}
				string[] array = requestLine.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
				Uri callbackUri;
				if (array.Length < 2 || !string.Equals(array[0], "GET", StringComparison.OrdinalIgnoreCase))
				{
					LogRejectionOnce(HttpStatusCode.MethodNotAllowed, (array.Length > 1) ? array[1] : "-");
					await WriteResponseAsync(stream, HttpStatusCode.MethodNotAllowed, "Method Not Allowed", ct).ConfigureAwait(continueOnCapturedContext: false);
					result = null;
				}
				else if (!Uri.TryCreate(_redirectUri, array[1], out callbackUri) || !string.Equals(callbackUri.AbsolutePath, _redirectUri.AbsolutePath, StringComparison.Ordinal))
				{
					LogRejectionOnce(HttpStatusCode.NotFound, array[1]);
					await WriteResponseAsync(stream, HttpStatusCode.NotFound, "Not Found", ct).ConfigureAwait(continueOnCapturedContext: false);
					result = null;
				}
				else
				{
					await WriteResponseAsync(stream, HttpStatusCode.OK, "<!doctype html>\r\n<html lang=\"en\">\r\n<head>\r\n  <meta charset=\"utf-8\">\r\n  <meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\r\n  <title>Kuro Sign-In Complete</title>\r\n  <style>\r\n    body {\r\n      margin: 0;\r\n      min-height: 100vh;\r\n      display: grid;\r\n      place-items: center;\r\n      font-family: -apple-system, BlinkMacSystemFont, \"Segoe UI\", sans-serif;\r\n      background: #f7f7f7;\r\n      color: #050505;\r\n    }\r\n    main {\r\n      width: min(420px, calc(100vw - 32px));\r\n      padding: 32px 28px;\r\n      border: 1px solid #e4e4e4;\r\n      border-radius: 12px;\r\n      background: white;\r\n      text-align: center;\r\n      box-shadow: 0 10px 30px rgba(0,0,0,.08);\r\n    }\r\n    h1 { margin: 0 0 12px; font-size: 24px; }\r\n    p { margin: 0 0 6px; color: #666; line-height: 1.5; }\r\n    .hint { margin-top: 14px; font-size: 13px; color: #999; }\r\n  </style>\r\n</head>\r\n<body>\r\n  <main>\r\n    <h1>Signed in!</h1>\r\n    <p>You can close this tab.</p>\r\n    <p class=\"hint\">Switch back to the Kuro app on your taskbar to finish onboarding.</p>\r\n  </main>\r\n</body>\r\n</html>", ct, "text/html; charset=utf-8").ConfigureAwait(continueOnCapturedContext: false);
					string text = callbackUri.ToString();
					Log("loopback-callback: received callback: " + text, "HandleClientAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Auth\\LoopbackOAuthCallbackServer.cs", 149);
					result = text;
				}
			}
		}
		return result;
	}

	private void LogRejectionOnce(HttpStatusCode status, string rawPath)
	{
		if (!_rejectionLogged)
		{
			_rejectionLogged = true;
			int num = rawPath.IndexOf('?');
			string text = ((num >= 0) ? rawPath.Substring(0, num) : rawPath);
			if (text.Length > 64)
			{
				text = text.Substring(0, 64);
			}
			Log($"loopback-callback: rejected request status={(int)status} path={text}", "LogRejectionOnce", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Auth\\LoopbackOAuthCallbackServer.cs", 167);
		}
	}

	private static async Task WriteResponseAsync(Stream stream, HttpStatusCode status, string body, CancellationToken ct, string contentType = "text/plain; charset=utf-8")
	{
		byte[] bodyBytes = Encoding.UTF8.GetBytes(body);
		string value = status switch
		{
			HttpStatusCode.OK => "OK", 
			HttpStatusCode.BadRequest => "Bad Request", 
			HttpStatusCode.MethodNotAllowed => "Method Not Allowed", 
			HttpStatusCode.NotFound => "Not Found", 
			_ => status.ToString(), 
		};
		string s = $"HTTP/1.1 {(int)status} {value}\r\nContent-Type: {contentType}\r\nContent-Length: {bodyBytes.Length}\r\n" + "Connection: close\r\n\r\n";
		byte[] bytes = Encoding.ASCII.GetBytes(s);
		await stream.WriteAsync(bytes, ct).ConfigureAwait(continueOnCapturedContext: false);
		await stream.WriteAsync(bodyBytes, ct).ConfigureAwait(continueOnCapturedContext: false);
		await stream.FlushAsync(ct).ConfigureAwait(continueOnCapturedContext: false);
	}

	private void StopListener()
	{
		try
		{
			_listener?.Stop();
		}
		catch
		{
		}
	}

	public void Dispose()
	{
		StopListener();
		_listener = null;
	}

	private static void Log(string message, [CallerMemberName] string memberName = "", [CallerFilePath] string filePath = "", [CallerLineNumber] int lineNumber = 0)
	{
		KuroLog.Auth(message, memberName, filePath, lineNumber);
	}
}
