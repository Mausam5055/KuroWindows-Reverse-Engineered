using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Threading.Tasks;
using ClientTelemetry;
using KuroWindows.Auth;
using KuroWindows.Capture;
using KuroWindows.RecorderCore.Capture;
using KuroWindows.RecorderCore.Segments;

namespace KuroWindows;

internal static class TelemetryMap
{
	public static UploadFailReason UploadFailReasonFor(ErrorKind kind)
	{
		switch (kind)
		{
		case ErrorKind.Network:
		case ErrorKind.Timeout:
		case ErrorKind.Tls:
			return UploadFailReason.Network;
		case ErrorKind.Http4xx:
		case ErrorKind.Auth:
		case ErrorKind.Blocked:
		case ErrorKind.UpdateRequired:
			return UploadFailReason.Http4xx;
		case ErrorKind.Http5xx:
			return UploadFailReason.Http5xx;
		case ErrorKind.Disk:
			return UploadFailReason.Disk;
		case ErrorKind.PresignRejected:
			return UploadFailReason.PresignRejected;
		case ErrorKind.DigestMismatch:
			return UploadFailReason.DigestMismatch;
		case ErrorKind.UploadGone:
			return UploadFailReason.UploadGone;
		case ErrorKind.IntegrityHeal:
			return UploadFailReason.IntegrityHeal;
		default:
			return UploadFailReason.Other;
		}
	}

	public static BlockReason BlockReasonFor(Exception? ex)
	{
		if (ex is APIClientException ex2)
		{
			if (ex2.IsUpdateRequired)
			{
				return BlockReason.UpdateRequired;
			}
			if (ex2.IsSessionExpired)
			{
				return BlockReason.Auth;
			}
			if (ex2.IsDeviceOrProfileBlocked)
			{
				return BlockReason.DeviceBanned;
			}
		}
		return BlockReason.ServerBlocked;
	}

	public static long? HttpStatusOf(Exception? ex)
	{
		if (!(ex is APIClientException ex2))
		{
			if (ex is HttpRequestException { StatusCode: { } statusCode })
			{
				return (long)statusCode;
			}
			return null;
		}
		return ex2.StatusCode;
	}

	public static AuthFailureReason AuthFailureReasonFor(Exception? ex)
	{
		if (ex is APIClientException { StatusCode: var statusCode })
		{
			if (statusCode < 500)
			{
				switch (statusCode)
				{
				case 408:
					return AuthFailureReason.Network;
				case 401:
					return AuthFailureReason.Expired;
				case 400:
				case 403:
					return AuthFailureReason.Revoked;
				case 429:
					break;
				default:
					return AuthFailureReason.Invalid;
				}
			}
			return AuthFailureReason.Unknown;
		}
		if (ex is HttpRequestException || ex is TaskCanceledException)
		{
			return AuthFailureReason.Network;
		}
		return AuthFailureReason.Unknown;
	}

	public static SessionEndReason SessionEndReasonFor(string? endReason)
	{
		switch (endReason)
		{
		case "user_stop":
		case "recording_not_expected":
			return SessionEndReason.User;
		case "system_sleep":
		case "idle":
		case "screen_lock":
		case "session_logoff":
		case "session_disconnect":
			return SessionEndReason.Idle;
		case "privacy_mask":
			return SessionEndReason.Privacy;
		case "low_disk":
			return SessionEndReason.LowDisk;
		case "force_update":
			return SessionEndReason.Update;
		case "app_quit":
			return SessionEndReason.Shutdown;
		case "desktop_sync_access_denied":
			return SessionEndReason.Policy;
		case "capture_access_denied":
			return SessionEndReason.CaptureLost;
		case "capture_failure":
			return SessionEndReason.Error;
		case "topology_change":
		case "unsupported_display_topology":
			return SessionEndReason.Topology;
		default:
			if (endReason != null && endReason.StartsWith("auth", StringComparison.Ordinal))
			{
				return SessionEndReason.Policy;
			}
			return SessionEndReason.Other;
		}
	}

	public static CaptureDenialClass DenialClassFor(Exception ex)
	{
		return CaptureAccessDeniedClassifier.LoggableHResult(ex) switch
		{
			-2147024891 => CaptureDenialClass.LockScreen, 
			-2005270485 => CaptureDenialClass.SecureDesktop, 
			_ => CaptureDenialClass.Other, 
		};
	}

	public static Endpoint EndpointFor(string host)
	{
		if (host.Equals(AppConfig.TelemetryIngestUrl.Host, StringComparison.OrdinalIgnoreCase))
		{
			return Endpoint.Api;
		}
		if (host.Contains("auth.", StringComparison.OrdinalIgnoreCase) || host.Contains("supabase", StringComparison.OrdinalIgnoreCase))
		{
			return Endpoint.Auth;
		}
		if (host.Contains("updates", StringComparison.OrdinalIgnoreCase))
		{
			return Endpoint.Updates;
		}
		if (host.Contains("amazonaws", StringComparison.OrdinalIgnoreCase) || host.Contains("s3", StringComparison.OrdinalIgnoreCase))
		{
			return Endpoint.Storage;
		}
		return Endpoint.Api;
	}

	public static TelemetryLevel? TelemetryLevelFor(string? level)
	{
		return level switch
		{
			"required" => (TelemetryLevel?)TelemetryLevel.Required, 
			"standard" => TelemetryLevel.Standard, 
			"verbose" => TelemetryLevel.Verbose, 
			_ => null, 
		};
	}

	public static CaptureRecoveryLabel RecoveryAttemptBucket(int attempt)
	{
		if (attempt > 1)
		{
			if (attempt <= 3)
			{
				return CaptureRecoveryLabel.Attempt23;
			}
			return CaptureRecoveryLabel.Attempt4Plus;
		}
		return CaptureRecoveryLabel.Attempt1;
	}

	public static ClientTelemetry.AuthState AuthStateFor(KuroWindows.Auth.AuthState state)
	{
		switch (state)
		{
		case KuroWindows.Auth.AuthState.SignedOut:
			return ClientTelemetry.AuthState.SignedOut;
		case KuroWindows.Auth.AuthState.Restoring:
			return ClientTelemetry.AuthState.Restoring;
		case KuroWindows.Auth.AuthState.SigningIn:
			return ClientTelemetry.AuthState.SigningIn;
		case KuroWindows.Auth.AuthState.SignedIn:
			return ClientTelemetry.AuthState.SignedIn;
		case KuroWindows.Auth.AuthState.DeviceBanned:
		case KuroWindows.Auth.AuthState.UserBanned:
		case KuroWindows.Auth.AuthState.DeviceLinkedToOtherUser:
			return ClientTelemetry.AuthState.Blocked;
		default:
			throw new UnreachableException();
		}
	}

	public static AudioState AudioStateFor(string? state)
	{
		return state switch
		{
			"on" => AudioState.On, 
			"failed" => AudioState.Failed, 
			"interrupted" => AudioState.Interrupted, 
			_ => AudioState.Off, 
		};
	}

	public static RecordingCore? RecordingCoreFor(IReadOnlyList<SegmentDisplayManifest> displays)
	{
		return Agreed(displays, (SegmentDisplayManifest d) => EncoderFactsOf(d.EncoderBackend)?.Core, RecordingCore.Mixed);
	}

	public static EncoderKind? EncoderKindFor(IReadOnlyList<SegmentDisplayManifest> displays)
	{
		return Agreed(displays, (SegmentDisplayManifest d) => EncoderFactsOf(d.EncoderBackend)?.Encoder, EncoderKind.Mixed);
	}

	public static CaptureSource? CaptureSourceFor(IReadOnlyList<SegmentDisplayManifest> displays)
	{
		return Agreed(displays, (SegmentDisplayManifest d) => d.Backend switch
		{
			CaptureBackendKind.DesktopDuplication => (CaptureSource?)CaptureSource.Dxgi, 
			CaptureBackendKind.WindowsGraphicsCapture => CaptureSource.Wgc, 
			_ => null, 
		}, CaptureSource.Mixed);
	}

	private static (RecordingCore Core, EncoderKind Encoder)? EncoderFactsOf(string? encoderBackend)
	{
		if (!(encoderBackend == "mf_h264_software_cpu_memory"))
		{
			if (encoderBackend == "mf_h264_hardware_d3d11_texture")
			{
				return (RecordingCore.GpuV2, EncoderKind.Hardware);
			}
			return null;
		}
		return (RecordingCore.CpuV1, EncoderKind.Software);
	}

	private static T? Agreed<T>(IReadOnlyList<SegmentDisplayManifest> displays, Func<SegmentDisplayManifest, T?> fact, T mixed) where T : struct, Enum
	{
		T? result = null;
		foreach (SegmentDisplayManifest display in displays)
		{
			T? val = fact(display);
			if (val.HasValue)
			{
				T valueOrDefault = val.GetValueOrDefault();
				if (result.HasValue && !result.GetValueOrDefault().Equals(valueOrDefault))
				{
					return mixed;
				}
				result = valueOrDefault;
			}
		}
		return result;
	}
}
