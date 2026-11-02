using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using KuroWindows.Net;

namespace KuroWindows.Upload;

public static class S3Uploader
{
	private static readonly HttpClient _http = CreateClient();

	private static HttpClient CreateClient()
	{
		return new HttpClient(ResilientDns.CreateHandler())
		{
			Timeout = TimeSpan.FromMinutes(AppConfig.S3UploadTimeoutMinutes)
		};
	}

	public static async Task UploadFileAsync(string filePath, string presignedUrl, string contentType, IReadOnlyDictionary<string, string>? extraHeaders = null, Action<long, long>? onProgress = null, CancellationToken ct = default(CancellationToken))
	{
		using FileStream fs = File.OpenRead(filePath);
		using ProgressStreamContent content = new ProgressStreamContent(fs, 65536, onProgress);
		content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
		using (await PutAsync(content, presignedUrl, extraHeaders, Path.GetFileName(filePath), ct).ConfigureAwait(continueOnCapturedContext: false))
		{
		}
	}

	public static async Task<string> UploadFilePartAsync(string filePath, long offset, long length, string presignedUrl, IReadOnlyDictionary<string, string>? extraHeaders = null, Action<long, long>? onProgress = null, CancellationToken ct = default(CancellationToken), int? partNumber = null)
	{
		using FileStream fs = File.OpenRead(filePath);
		if (offset < 0 || length <= 0 || offset + length > fs.Length)
		{
			throw new InvalidOperationException($"Invalid upload range for {Path.GetFileName(filePath)}: offset={offset}, length={length}, file_length={fs.Length}");
		}
		fs.Seek(offset, SeekOrigin.Begin);
		using RangedProgressStreamContent content = new RangedProgressStreamContent(fs, length, 65536, onProgress);
		using HttpResponseMessage httpResponseMessage = await PutAsync(content, presignedUrl, extraHeaders, Path.GetFileName(filePath), ct, partNumber).ConfigureAwait(continueOnCapturedContext: false);
		return httpResponseMessage.Headers.ETag?.ToString() ?? "";
	}

	private static async Task<HttpResponseMessage> PutAsync(HttpContent content, string presignedUrl, IReadOnlyDictionary<string, string>? extraHeaders, string fileName, CancellationToken ct, int? partNumber = null)
	{
		ResilientDns.RegisterPresignedAuthority(presignedUrl);
		using HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Put, presignedUrl)
		{
			Content = content,
			Version = HttpVersion.Version11,
			VersionPolicy = HttpVersionPolicy.RequestVersionExact
		};
		req.Headers.ExpectContinue = false;
		if (extraHeaders != null)
		{
			foreach (var (text3, value) in extraHeaders)
			{
				if (!text3.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
				{
					req.Headers.TryAddWithoutValidation(text3, value);
				}
			}
		}
		HttpResponseMessage resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(continueOnCapturedContext: false);
		if (!resp.IsSuccessStatusCode)
		{
			using (resp)
			{
				string responseBody = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(continueOnCapturedContext: false);
				throw new S3UploadException(resp.StatusCode, fileName, responseBody, partNumber);
			}
		}
		return resp;
	}
}
