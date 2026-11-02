using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;

namespace ClientTelemetry;

internal sealed class HttpTransport : ITransport
{
	private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15.0);

	private readonly HttpClient _http;

	public HttpTransport(HttpMessageHandler? handler)
	{
		_http = ((handler == null) ? new HttpClient() : new HttpClient(handler, disposeHandler: false));
		_http.Timeout = Timeout;
	}

	public SendOutcome Send(Uri url, string bearer, ReadOnlyMemory<byte> body, CancellationToken ct)
	{
		try
		{
			using HttpRequestMessage httpRequestMessage = new HttpRequestMessage(HttpMethod.Post, url);
			httpRequestMessage.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
			httpRequestMessage.Content = new ReadOnlyMemoryContent(body);
			httpRequestMessage.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
			using HttpResponseMessage res = _http.Send(httpRequestMessage, HttpCompletionOption.ResponseHeadersRead, ct);
			return Classify(res);
		}
		catch (OperationCanceledException) when (ct.IsCancellationRequested)
		{
			throw;
		}
		catch (Exception)
		{
			return new SendOutcome(SendStatus.Retry);
		}
	}

	internal static SendOutcome Classify(HttpResponseMessage res)
	{
		int statusCode = (int)res.StatusCode;
		TimeSpan? retryAfter = RetryAfter(res.Headers.RetryAfter);
		HttpStatusCode statusCode2 = res.StatusCode;
		if (statusCode < 200 || statusCode >= 300)
		{
			switch (statusCode2)
			{
			case HttpStatusCode.Unauthorized:
				return new SendOutcome(SendStatus.Retry, null, statusCode);
			case HttpStatusCode.TooManyRequests:
				return new SendOutcome(SendStatus.Throttled, retryAfter, statusCode);
			case HttpStatusCode.RequestTimeout:
			case HttpStatusCode.BadGateway:
			case HttpStatusCode.ServiceUnavailable:
			case HttpStatusCode.GatewayTimeout:
				return new SendOutcome(SendStatus.Retry, retryAfter, statusCode);
			default:
				if (statusCode >= 500)
				{
					return new SendOutcome(SendStatus.Retry, retryAfter, statusCode);
				}
				return new SendOutcome(SendStatus.Rejected, null, statusCode);
			}
		}
		return new SendOutcome(SendStatus.Accepted, null, statusCode);
	}

	internal static TimeSpan? RetryAfter(RetryConditionHeaderValue? h)
	{
		if (h == null)
		{
			return null;
		}
		TimeSpan? delta = h.Delta;
		if (delta.HasValue)
		{
			TimeSpan valueOrDefault = delta.GetValueOrDefault();
			return (valueOrDefault > TimeSpan.Zero) ? valueOrDefault : TimeSpan.Zero;
		}
		DateTimeOffset? date = h.Date;
		if (date.HasValue)
		{
			DateTimeOffset valueOrDefault2 = date.GetValueOrDefault();
			TimeSpan timeSpan = valueOrDefault2 - DateTimeOffset.UtcNow;
			return (timeSpan > TimeSpan.Zero) ? timeSpan : TimeSpan.Zero;
		}
		return null;
	}
}
