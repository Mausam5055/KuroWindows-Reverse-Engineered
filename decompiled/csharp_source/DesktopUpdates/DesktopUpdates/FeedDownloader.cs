using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Velopack.Sources;

namespace DesktopUpdates;

internal sealed class FeedDownloader : IFileDownloader
{
	internal static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(60.0);

	internal static readonly TimeSpan FeedReadTimeout = TimeSpan.FromSeconds(30.0);

	private const string ResumeSuffix = ".resume";

	private const int BufferBytes = 81920;

	private readonly HttpClient _http;

	private readonly UpdateLog _log;

	private readonly object _resumeLock = new object();

	private readonly HashSet<string> _inFlight = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

	private long _bytesReceived;

	private int _fullFetched;

	private int _deltaFetched;

	public long BytesReceived => Interlocked.Read(in _bytesReceived);

	public bool LastBatchWasDelta
	{
		get
		{
			if (Volatile.Read(in _deltaFetched) != 0)
			{
				return Volatile.Read(in _fullFetched) == 0;
			}
			return false;
		}
	}

	public FeedDownloader(string feedUrl, Func<HttpMessageHandler> createHandler, Action<string>? registerHost, UpdateLog log)
	{
		_log = log;
		if (Uri.TryCreate(feedUrl, UriKind.Absolute, out Uri result))
		{
			registerHost?.Invoke(result.Host);
		}
		_http = new HttpClient(createHandler())
		{
			Timeout = Timeout.InfiniteTimeSpan
		};
		_http.DefaultRequestHeaders.UserAgent.Add(HttpClientFileDownloader.UserAgent);
	}

	public void BeginBatch()
	{
		Volatile.Write(ref _fullFetched, 0);
		Volatile.Write(ref _deltaFetched, 0);
	}

	public async Task<string> DownloadString(string url, IDictionary<string, string>? headers, double timeout)
	{
		using CancellationTokenSource cts = new CancellationTokenSource(FeedReadTimeout);
		using HttpRequestMessage request = Request(url, headers);
		_ = 1;
		try
		{
			using HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(continueOnCapturedContext: false);
			response.EnsureSuccessStatusCode();
			return await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(continueOnCapturedContext: false);
		}
		catch (OperationCanceledException) when (cts.IsCancellationRequested)
		{
			throw new TimeoutException($"update feed read timed out after {(int)FeedReadTimeout.TotalSeconds}s");
		}
	}

	public async Task<byte[]> DownloadBytes(string url, IDictionary<string, string>? headers, double timeout)
	{
		using CancellationTokenSource cts = new CancellationTokenSource(FeedReadTimeout);
		using HttpRequestMessage request = Request(url, headers);
		_ = 1;
		try
		{
			using HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(continueOnCapturedContext: false);
			response.EnsureSuccessStatusCode();
			return await response.Content.ReadAsByteArrayAsync(cts.Token).ConfigureAwait(continueOnCapturedContext: false);
		}
		catch (OperationCanceledException) when (cts.IsCancellationRequested)
		{
			throw new TimeoutException($"update feed read timed out after {(int)FeedReadTimeout.TotalSeconds}s");
		}
	}

	public async Task DownloadFile(string url, string targetFile, Action<int> progress, IDictionary<string, string>? headers, double timeout, CancellationToken cancelToken = default(CancellationToken))
	{
		string resumePath = targetFile + ".resume";
		lock (_resumeLock)
		{
			_inFlight.Add(resumePath);
			if (_inFlight.Count == 1)
			{
				DeleteStaleResumeFiles(resumePath);
			}
		}
		try
		{
			await DownloadResumableAsync(url, targetFile, resumePath, progress, headers, cancelToken).ConfigureAwait(continueOnCapturedContext: false);
		}
		finally
		{
			lock (_resumeLock)
			{
				_inFlight.Remove(resumePath);
			}
		}
		if (Path.GetFileName(targetFile).Contains("-delta.nupkg", StringComparison.OrdinalIgnoreCase))
		{
			Volatile.Write(ref _deltaFetched, 1);
		}
		else
		{
			Volatile.Write(ref _fullFetched, 1);
		}
	}

	private async Task DownloadResumableAsync(string url, string targetFile, string resumePath, Action<int> progress, IDictionary<string, string>? headers, CancellationToken cancelToken)
	{
		int pass = 0;
		while (true)
		{
			long have = (File.Exists(resumePath) ? new FileInfo(resumePath).Length : 0);
			using (CancellationTokenSource stall = CancellationTokenSource.CreateLinkedTokenSource(cancelToken))
			{
				stall.CancelAfter(StallTimeout);
				using HttpRequestMessage request = Request(url, headers);
				if (have > 0)
				{
					request.Headers.Range = new RangeHeaderValue(have, null);
				}
				HttpResponseMessage httpResponseMessage;
				try
				{
					httpResponseMessage = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stall.Token).ConfigureAwait(continueOnCapturedContext: false);
				}
				catch (OperationCanceledException) when (!cancelToken.IsCancellationRequested && stall.IsCancellationRequested)
				{
					throw Stalled(have);
				}
				using (httpResponseMessage)
				{
					if (have <= 0 || httpResponseMessage.StatusCode != HttpStatusCode.RequestedRangeNotSatisfiable || pass != 0)
					{
						httpResponseMessage.EnsureSuccessStatusCode();
						bool append = have > 0 && httpResponseMessage.StatusCode == HttpStatusCode.PartialContent;
						long? total = ((!append) ? httpResponseMessage.Content.Headers.ContentLength : httpResponseMessage.Content.Headers.ContentRange?.Length);
						long done = (append ? have : 0);
						await using (Stream source = await httpResponseMessage.Content.ReadAsStreamAsync(stall.Token).ConfigureAwait(continueOnCapturedContext: false))
						{
							await using FileStream file = new FileStream(resumePath, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
							byte[] buffer = new byte[81920];
							int lastPercent = -1;
							while (true)
							{
								int read;
								try
								{
									read = await source.ReadAsync(buffer, stall.Token).ConfigureAwait(continueOnCapturedContext: false);
								}
								catch (OperationCanceledException) when (!cancelToken.IsCancellationRequested && stall.IsCancellationRequested)
								{
									throw Stalled(done);
								}
								if (read == 0)
								{
									break;
								}
								await file.WriteAsync(buffer.AsMemory(0, read), cancelToken).ConfigureAwait(continueOnCapturedContext: false);
								done += read;
								Interlocked.Add(ref _bytesReceived, read);
								stall.CancelAfter(StallTimeout);
								if (total.HasValue && total.GetValueOrDefault() > 0)
								{
									int num = (int)Math.Min(100L, done * 100 / total.Value);
									if (num != lastPercent)
									{
										lastPercent = num;
										progress?.Invoke(num);
									}
								}
							}
						}
						if (total.HasValue)
						{
							long valueOrDefault = total.GetValueOrDefault();
							if (done != valueOrDefault)
							{
								throw new IOException($"update download ended at {done} of {valueOrDefault} bytes");
							}
						}
						File.Move(resumePath, targetFile, overwrite: true);
						progress?.Invoke(100);
						break;
					}
					TryDelete(resumePath);
				}
			}
			pass++;
		}
	}

	private static HttpRequestMessage Request(string url, IDictionary<string, string>? headers)
	{
		HttpRequestMessage httpRequestMessage = new HttpRequestMessage(HttpMethod.Get, url);
		if (headers != null)
		{
			foreach (var (name, value) in headers)
			{
				httpRequestMessage.Headers.TryAddWithoutValidation(name, value);
			}
		}
		return httpRequestMessage;
	}

	private static UpdateDownloadStalledException Stalled(long bytesOnDisk)
	{
		return new UpdateDownloadStalledException($"update download stalled: no bytes for {(int)StallTimeout.TotalSeconds}s at {bytesOnDisk} bytes");
	}

	private void DeleteStaleResumeFiles(string keep)
	{
		try
		{
			string directoryName = Path.GetDirectoryName(keep);
			if (string.IsNullOrEmpty(directoryName) || !Directory.Exists(directoryName))
			{
				return;
			}
			foreach (string item in Directory.EnumerateFiles(directoryName, "*.resume"))
			{
				if (!string.Equals(item, keep, StringComparison.OrdinalIgnoreCase))
				{
					TryDelete(item);
				}
			}
		}
		catch (Exception ex)
		{
			_log.LocalOnly("update download: stale resume cleanup failed: " + _log.ExceptionSummary(ex), "DeleteStaleResumeFiles", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\shared\\dotnet\\DesktopUpdates\\FeedDownloader.cs", 282);
		}
	}

	private void TryDelete(string path)
	{
		try
		{
			File.Delete(path);
		}
		catch (Exception ex)
		{
			_log.LocalOnly("update download: delete " + Path.GetFileName(path) + " failed: " + _log.ExceptionSummary(ex), "TryDelete", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\shared\\dotnet\\DesktopUpdates\\FeedDownloader.cs", 294);
		}
	}
}
