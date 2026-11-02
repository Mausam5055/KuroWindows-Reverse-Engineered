using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Authentication;
using System.Threading.Tasks;
using ClientTelemetry;
using KuroWindows.Auth;

namespace KuroWindows.Upload;

internal static class UploadFailurePolicy
{
	private const string UploadRequestOperation = "desktop-v2-upload-request";

	private const string UploadCompleteOperation = "desktop-v2-upload-complete";

	private const string MultipartCompleteOperation = "desktop-v2-upload-multipart-complete";

	private static bool IsHealCode(string? code)
	{
		switch (code)
		{
		case "missing_uploaded_object":
		case "size_mismatch":
		case "hash_metadata_mismatch":
			return true;
		default:
			return false;
		}
	}

	public static UploadReaction? HealReaction(string? code)
	{
		return code switch
		{
			"missing_uploaded_object" => (UploadReaction?)UploadReaction.HealMissingObject, 
			"size_mismatch" => UploadReaction.HealSizeMismatch, 
			"hash_metadata_mismatch" => UploadReaction.HealHashMismatch, 
			_ => null, 
		};
	}

	public static UploadFailure Classify(Exception ex)
	{
		if (!(ex is SegmentFateRecordedException ex2))
		{
			if (!(ex is UploadDigestRejectedException ex3))
			{
				if (!(ex is MultipartCompleteRejectedException ex4))
				{
					if (!(ex is S3UploadException s))
					{
						if (!(ex is APIClientException api))
						{
							if (!(ex is AuthenticationException))
							{
								if (!(ex is TaskCanceledException))
								{
									if (!(ex is OperationCanceledException))
									{
										if (!(ex is HttpRequestException ex5))
										{
											if (ex is IOException || ex is UnauthorizedAccessException)
											{
												return Plain(ex, ErrorKind.Disk);
											}
											Exception innerException = ex.InnerException;
											UploadFailure uploadFailure = ((innerException != null && innerException != ex) ? Classify(innerException) : null);
											return Plain(ex, uploadFailure?.ErrorKind ?? ErrorKind.Unknown, retryInLoop: false, uploadFailure?.Phase ?? UploadPhase.Put);
										}
										int kind = ((ex5.InnerException is AuthenticationException) ? 4 : 0);
										HttpStatusCode? statusCode = ex5.StatusCode;
										long? httpStatus;
										if (statusCode.HasValue)
										{
											HttpStatusCode valueOrDefault = statusCode.GetValueOrDefault();
											httpStatus = (long)valueOrDefault;
										}
										else
										{
											httpStatus = null;
										}
										return Plain(ex, (ErrorKind)kind, retryInLoop: true, UploadPhase.Put, httpStatus, "network");
									}
									return Plain(ex, ErrorKind.Cancelled, retryInLoop: false, UploadPhase.Put, null, "cancelled");
								}
								return Plain(ex, ErrorKind.Timeout, retryInLoop: true, UploadPhase.Put, null, "timeout");
							}
							return Plain(ex, ErrorKind.Tls);
						}
						return ClassifyApi(api);
					}
					return ClassifyS3(s);
				}
				return new UploadFailure(RetryInLoop: false, S3UploadFailure.Other, ex4.Code, HealScope.File, ex4.FileName, UploadGone: false, ServerRejected: false, ErrorKind.IntegrityHeal, UploadPhase.MultipartComplete, null, ex4.Code, "multipart_complete_" + ex4.Code);
			}
			return Classify(ex3.Cause) with
			{
				RetryInLoop = false,
				S3 = S3UploadFailure.Other,
				ServerRejected = true,
				ErrorCode = "digest_rejected_unchanged",
				DurableErrorCode = "digest_rejected_unchanged"
			};
		}
		Exception innerException2 = ex2.InnerException;
		if (innerException2 == null || innerException2 == ex)
		{
			long? httpStatus2 = null;
			return Plain(ex, ErrorKind.Unknown, retryInLoop: false, UploadPhase.Put, httpStatus2) with
			{
				ErrorCode = ex2.ErrorCode,
				DurableErrorCode = ex2.ErrorCode
			};
		}
		return Classify(innerException2);
	}

	private static UploadFailure ClassifyS3(S3UploadException s3)
	{
		int statusCode = (int)s3.StatusCode;
		ErrorKind errorKind = s3.Failure switch
		{
			S3UploadFailure.PresignRejected => ErrorKind.PresignRejected, 
			S3UploadFailure.DigestMismatch => ErrorKind.DigestMismatch, 
			S3UploadFailure.NoSuchUpload => ErrorKind.UploadGone, 
			_ => (statusCode >= 500) ? ErrorKind.Http5xx : ErrorKind.Http4xx, 
		};
		string errorCode = s3.Failure switch
		{
			S3UploadFailure.PresignRejected => "presign_rejected", 
			S3UploadFailure.DigestMismatch => "digest_mismatch", 
			S3UploadFailure.ObjectExists => "object_exists", 
			S3UploadFailure.NoSuchUpload => "upload_gone", 
			S3UploadFailure.PayloadTooLarge => "payload_too_large", 
			_ => $"s3_{statusCode}", 
		};
		bool flag = statusCode == 400 && (s3.ResponseBody?.Contains("RequestTimeout", StringComparison.OrdinalIgnoreCase) ?? false);
		bool serverRejected = s3.Failure switch
		{
			S3UploadFailure.DigestMismatch => true, 
			S3UploadFailure.PayloadTooLarge => true, 
			S3UploadFailure.Other => statusCode >= 400 && statusCode < 500 && !flag, 
			_ => false, 
		};
		return new UploadFailure((statusCode >= 500 || statusCode == 429) | flag, s3.Failure, null, null, null, s3.Failure == S3UploadFailure.NoSuchUpload, serverRejected, errorKind, (!s3.PartNumber.HasValue) ? UploadPhase.Put : UploadPhase.MultipartPart, statusCode, errorCode, $"s3_{statusCode}");
	}

	private static UploadFailure ClassifyApi(APIClientException api)
	{
		int statusCode = api.StatusCode;
		bool flag = api.IsSessionExpired || api.IsUpdateRequired || api.IsDeviceOrProfileBlocked;
		bool flag2 = api.IsRetryable && !flag;
		bool flag3;
		switch (api.Operation)
		{
		case "desktop-v2-upload-request":
		case "desktop-v2-upload-complete":
		case "desktop-v2-upload-multipart-complete":
			flag3 = true;
			break;
		default:
			flag3 = false;
			break;
		}
		bool flag4 = flag3;
		S3UploadFailure s3UploadFailure = ((api.Operation == "desktop-v2-upload-multipart-complete" && api.Code == "multipart_upload_not_found") ? S3UploadFailure.NoSuchUpload : S3UploadFailure.Other);
		string operation = api.Operation;
		flag3 = ((operation == "desktop-v2-upload-complete" || operation == "desktop-v2-upload-multipart-complete") ? true : false);
		string text = ((flag3 && IsHealCode(api.Code)) ? api.Code : null);
		HealScope? healScope = ((text == null) ? ((HealScope?)null) : new HealScope?((!(api.Operation == "desktop-v2-upload-complete")) ? HealScope.File : HealScope.Segment));
		ErrorKind errorKind;
		int num;
		if (api != null)
		{
			if (api.Code == "multipart_upload_not_found")
			{
				errorKind = ErrorKind.UploadGone;
				goto IL_0203;
			}
			num = 0;
		}
		else
		{
			num = 4;
		}
		if (IsHealCode(api.Code))
		{
			goto IL_0182;
		}
		if (num != 0)
		{
			if (num != 4)
			{
				goto IL_0182;
			}
			num = 5;
		}
		else
		{
			num = 1;
		}
		if (statusCode == 401 || api.IsSessionExpired)
		{
			goto IL_01aa;
		}
		if (num != 1)
		{
			if (num != 5)
			{
				goto IL_01aa;
			}
			num = 6;
		}
		else
		{
			num = 2;
		}
		if (api.IsUpdateRequired)
		{
			goto IL_01c9;
		}
		if (num != 2)
		{
			if (num != 6)
			{
				goto IL_01c9;
			}
			num = 7;
		}
		else
		{
			num = 3;
		}
		if (!api.IsDeviceOrProfileBlocked)
		{
			if (num == 3)
			{
				int statusCode2 = api.StatusCode;
				if (statusCode2 < 500)
				{
					if (statusCode2 < 400)
					{
						goto IL_01ff;
					}
					errorKind = ((statusCode2 == 408) ? ErrorKind.Timeout : ErrorKind.Http4xx);
				}
				else
				{
					errorKind = ErrorKind.Http5xx;
				}
				goto IL_0203;
			}
			if (num == 7)
			{
				goto IL_01ff;
			}
		}
		errorKind = ErrorKind.Blocked;
		goto IL_0203;
		IL_0182:
		errorKind = ErrorKind.IntegrityHeal;
		goto IL_0203;
		IL_01ff:
		errorKind = ErrorKind.Unknown;
		goto IL_0203;
		IL_0203:
		ErrorKind errorKind2 = errorKind;
		UploadPhase phase = api.Operation switch
		{
			"desktop-v2-upload-request" => UploadPhase.Request, 
			"desktop-v2-upload-multipart-complete" => UploadPhase.MultipartComplete, 
			"desktop-v2-upload-complete" => UploadPhase.Complete, 
			_ => UploadPhase.Put, 
		};
		flag3 = flag4;
		if (flag3)
		{
			bool flag5;
			switch (api.Code)
			{
			case "device_proof_required":
			case "device_key_mismatch":
			case "device_proof_invalid":
				flag5 = true;
				break;
			default:
				flag5 = false;
				break;
			}
			flag3 = flag5;
		}
		bool flag6 = flag3;
		return new UploadFailure(flag2, s3UploadFailure, text, healScope, null, s3UploadFailure == S3UploadFailure.NoSuchUpload, flag4 && statusCode >= 400 && statusCode < 500 && statusCode != 401 && statusCode != 426 && !flag && !flag2 && !flag6, errorKind2, phase, statusCode, string.IsNullOrEmpty(api.Code) ? api.GetType().Name : api.Code, string.IsNullOrWhiteSpace(api.Code) ? $"api_{statusCode}" : $"api_{statusCode}_{api.Code}");
		IL_01aa:
		errorKind = ErrorKind.Auth;
		goto IL_0203;
		IL_01c9:
		errorKind = ErrorKind.UpdateRequired;
		goto IL_0203;
	}

	public static bool RecoveryResetsMultipartState(Exception ex)
	{
		if (ex is MultipartCompleteRejectedException)
		{
			return false;
		}
		if (ex is InvalidOperationException)
		{
			return true;
		}
		UploadFailure uploadFailure = Classify(ex);
		if (!uploadFailure.UploadGone)
		{
			return uploadFailure.ServerRejected;
		}
		return true;
	}

	private static UploadFailure Plain(Exception ex, ErrorKind kind, bool retryInLoop = false, UploadPhase phase = UploadPhase.Put, long? httpStatus = null, string? durableErrorCode = null)
	{
		return new UploadFailure(retryInLoop, S3UploadFailure.Other, null, null, null, UploadGone: false, ServerRejected: false, kind, phase, httpStatus, ex.GetType().Name, durableErrorCode ?? ex.GetType().Name);
	}
}
