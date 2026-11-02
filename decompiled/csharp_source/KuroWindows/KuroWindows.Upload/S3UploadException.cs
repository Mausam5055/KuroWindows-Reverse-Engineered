using System;
using System.Linq;
using System.Net;

namespace KuroWindows.Upload;

public sealed class S3UploadException(HttpStatusCode statusCode, string fileName, string? responseBody = null, int? partNumber = null) : Exception(BuildMessage(statusCode, fileName, responseBody))
{
	public HttpStatusCode StatusCode { get; } = statusCode;

	public string FileName { get; } = fileName;

	public string? ResponseBody { get; } = responseBody;

	public int? PartNumber { get; } = partNumber;

	public S3UploadFailure Failure { get; } = Classify(statusCode, responseBody);

	internal static S3UploadFailure Classify(HttpStatusCode statusCode, string? body)
	{
		switch (statusCode)
		{
		case HttpStatusCode.Unauthorized:
		case HttpStatusCode.Forbidden:
			return S3UploadFailure.PresignRejected;
		case HttpStatusCode.PreconditionFailed:
			return S3UploadFailure.ObjectExists;
		case HttpStatusCode.RequestEntityTooLarge:
			return S3UploadFailure.PayloadTooLarge;
		case HttpStatusCode.NotFound:
			if (BodyHas(new string[1] { "NoSuchUpload" }))
			{
				return S3UploadFailure.NoSuchUpload;
			}
			break;
		case HttpStatusCode.BadRequest:
			if (BodyHas(new string[2] { "BadDigest", "XAmzContentSHA256Mismatch" }))
			{
				return S3UploadFailure.DigestMismatch;
			}
			if (BodyHas(new string[5] { "SignatureDoesNotMatch", "ExpiredToken", "RequestTimeTooSkewed", "AuthorizationHeaderMalformed", "InvalidToken" }))
			{
				return S3UploadFailure.PresignRejected;
			}
			break;
		}
		return S3UploadFailure.Other;
		bool BodyHas(params string[] codes)
		{
			if (body != null)
			{
				return codes.Any((string code) => body.Contains(code, StringComparison.OrdinalIgnoreCase));
			}
			return false;
		}
	}

	private static string BuildMessage(HttpStatusCode statusCode, string fileName, string? responseBody)
	{
		string text = $"Upload failed with HTTP {(int)statusCode} for {fileName}";
		if (string.IsNullOrWhiteSpace(responseBody))
		{
			return text;
		}
		string text2 = responseBody.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal).Trim();
		if (text2.Length > 500)
		{
			return text + ": " + text2.Substring(0, 500) + "...";
		}
		return text + ": " + text2;
	}
}
