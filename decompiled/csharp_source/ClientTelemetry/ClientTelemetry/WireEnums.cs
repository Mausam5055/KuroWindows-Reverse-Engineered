namespace ClientTelemetry;

public static class WireEnums
{
	public static string ToWire(this ErrorKind v)
	{
		return v switch
		{
			ErrorKind.Network => "network", 
			ErrorKind.Timeout => "timeout", 
			ErrorKind.Http4xx => "http_4xx", 
			ErrorKind.Http5xx => "http_5xx", 
			ErrorKind.Tls => "tls", 
			ErrorKind.Disk => "disk", 
			ErrorKind.Auth => "auth", 
			ErrorKind.Blocked => "blocked", 
			ErrorKind.UpdateRequired => "update_required", 
			ErrorKind.Cancelled => "cancelled", 
			ErrorKind.PresignRejected => "presign_rejected", 
			ErrorKind.DigestMismatch => "digest_mismatch", 
			ErrorKind.UploadGone => "upload_gone", 
			ErrorKind.IntegrityHeal => "integrity_heal", 
			ErrorKind.Unknown => "unknown", 
			_ => "unknown", 
		};
	}

	public static string ToWire(this UploadPhase v)
	{
		return v switch
		{
			UploadPhase.Request => "request", 
			UploadPhase.Put => "put", 
			UploadPhase.MultipartPart => "multipart_part", 
			UploadPhase.MultipartComplete => "multipart_complete", 
			UploadPhase.Complete => "complete", 
			UploadPhase.Local => "local", 
			_ => "unknown", 
		};
	}

	public static string ToWire(this CaptureBackend v)
	{
		return v switch
		{
			CaptureBackend.CpuV1 => "cpu_v1", 
			CaptureBackend.Screencapturekit => "screencapturekit", 
			_ => "unknown", 
		};
	}

	public static string ToWire(this RecordingCore v)
	{
		return v switch
		{
			RecordingCore.CpuV1 => "cpu_v1", 
			RecordingCore.GpuV2 => "gpu_v2", 
			RecordingCore.Mixed => "mixed", 
			_ => "unknown", 
		};
	}

	public static string ToWire(this CaptureSource v)
	{
		return v switch
		{
			CaptureSource.Dxgi => "dxgi", 
			CaptureSource.Wgc => "wgc", 
			CaptureSource.Screencapturekit => "screencapturekit", 
			CaptureSource.Mixed => "mixed", 
			_ => "unknown", 
		};
	}

	public static string ToWire(this EncoderKind v)
	{
		return v switch
		{
			EncoderKind.Software => "software", 
			EncoderKind.Hardware => "hardware", 
			EncoderKind.Mixed => "mixed", 
			_ => "unknown", 
		};
	}

	public static string ToWire(this CaptureDenialClass v)
	{
		return v switch
		{
			CaptureDenialClass.LockScreen => "lock_screen", 
			CaptureDenialClass.SecureDesktop => "secure_desktop", 
			CaptureDenialClass.Other => "other", 
			_ => "unknown", 
		};
	}

	public static string ToWire(this SessionEndReason v)
	{
		return v switch
		{
			SessionEndReason.User => "user", 
			SessionEndReason.Idle => "idle", 
			SessionEndReason.Privacy => "privacy", 
			SessionEndReason.LowDisk => "low_disk", 
			SessionEndReason.Update => "update", 
			SessionEndReason.Shutdown => "shutdown", 
			SessionEndReason.Policy => "policy", 
			SessionEndReason.Error => "error", 
			SessionEndReason.Topology => "topology", 
			SessionEndReason.CaptureLost => "capture_lost", 
			SessionEndReason.Other => "other", 
			_ => "unknown", 
		};
	}

	public static string ToWire(this SessionStartFailure v)
	{
		return v switch
		{
			SessionStartFailure.CaptureFailed => "capture_failed", 
			SessionStartFailure.TopologyUnsupported => "topology_unsupported", 
			_ => "unknown", 
		};
	}

	public static string ToWire(this SegmentDisposition v)
	{
		return v switch
		{
			SegmentDisposition.Complete => "complete", 
			SegmentDisposition.Suspect => "suspect", 
			_ => "unknown", 
		};
	}

	public static string ToWire(this FateOrigin v)
	{
		return v switch
		{
			FateOrigin.Seal => "seal", 
			FateOrigin.LaunchRecovery => "launch_recovery", 
			FateOrigin.Discovery => "discovery", 
			FateOrigin.Upload => "upload", 
			_ => "unknown", 
		};
	}

	public static string ToWire(this QuarantineReason v)
	{
		return v switch
		{
			QuarantineReason.EncodeFailed => "encode_failed", 
			QuarantineReason.ZeroFrames => "zero_frames", 
			QuarantineReason.FileMissing => "file_missing", 
			QuarantineReason.ContainerInvalid => "container_invalid", 
			QuarantineReason.FinalizeTimeout => "finalize_timeout", 
			QuarantineReason.ManifestWriteFailed => "manifest_write_failed", 
			QuarantineReason.NoDisplays => "no_displays", 
			QuarantineReason.DurationZero => "duration_zero", 
			QuarantineReason.SidecarInvalid => "sidecar_invalid", 
			QuarantineReason.TimestampUnresolved => "timestamp_unresolved", 
			QuarantineReason.RecoveryFailed => "recovery_failed", 
			QuarantineReason.HealEscalated => "heal_escalated", 
			QuarantineReason.Legacy => "legacy", 
			_ => "unknown", 
		};
	}

	public static string ToWire(this DeleteCause v)
	{
		return v switch
		{
			DeleteCause.BacklogPurged => "backlog_purged", 
			DeleteCause.QuarantineExpired => "quarantine_expired", 
			DeleteCause.CorruptManifestExpired => "corrupt_manifest_expired", 
			DeleteCause.OrphanExpired => "orphan_expired", 
			DeleteCause.DebrisExpired => "debris_expired", 
			DeleteCause.AlreadyUploadedCollision => "already_uploaded_collision", 
			_ => "unknown", 
		};
	}

	public static string ToWire(this AuthState v)
	{
		return v switch
		{
			AuthState.SignedOut => "signed_out", 
			AuthState.Restoring => "restoring", 
			AuthState.SigningIn => "signing_in", 
			AuthState.SignedIn => "signed_in", 
			AuthState.Blocked => "blocked", 
			_ => "unknown", 
		};
	}

	public static string ToWire(this AuthFailureReason v)
	{
		return v switch
		{
			AuthFailureReason.Expired => "expired", 
			AuthFailureReason.Revoked => "revoked", 
			AuthFailureReason.Network => "network", 
			AuthFailureReason.Invalid => "invalid", 
			AuthFailureReason.Unknown => "unknown", 
			_ => "unknown", 
		};
	}

	public static string ToWire(this SignoutTrigger v)
	{
		return v switch
		{
			SignoutTrigger.Upload401 => "upload_401", 
			SignoutTrigger.Api401 => "api_401", 
			SignoutTrigger.RefreshRejected => "refresh_rejected", 
			_ => "unknown", 
		};
	}

	public static string ToWire(this TelemetryLevel v)
	{
		return v switch
		{
			TelemetryLevel.Required => "required", 
			TelemetryLevel.Standard => "standard", 
			TelemetryLevel.Verbose => "verbose", 
			_ => "unknown", 
		};
	}

	public static string ToWire(this UpdateStage v)
	{
		return v switch
		{
			UpdateStage.Download => "download", 
			UpdateStage.Apply => "apply", 
			UpdateStage.Check => "check", 
			UpdateStage.InstallLocation => "install_location", 
			UpdateStage.Permission => "permission", 
			_ => "unknown", 
		};
	}

	public static string ToWire(this UpdateTrigger v)
	{
		return v switch
		{
			UpdateTrigger.Heartbeat => "heartbeat", 
			UpdateTrigger.Required => "required", 
			UpdateTrigger.User => "user", 
			UpdateTrigger.Launch => "launch", 
			UpdateTrigger.Quit => "quit", 
			_ => "unknown", 
		};
	}

	public static string ToWire(this CrashKind v)
	{
		return v switch
		{
			CrashKind.UnhandledException => "unhandled_exception", 
			CrashKind.NativeSignal => "native_signal", 
			CrashKind.WerDump => "wer_dump", 
			CrashKind.Metrickit => "metrickit", 
			CrashKind.Hang => "hang", 
			_ => "unknown", 
		};
	}

	public static string ToWire(this CrashStackSource v)
	{
		return v switch
		{
			CrashStackSource.Ips => "ips", 
			CrashStackSource.Handler => "handler", 
			CrashStackSource.Metrickit => "metrickit", 
			_ => "unknown", 
		};
	}

	public static string ToWire(this ClockJumpSource v)
	{
		return v switch
		{
			ClockJumpSource.Detector => "detector", 
			ClockJumpSource.WakeRelatch => "wake_relatch", 
			_ => "unknown", 
		};
	}

	public static string ToWire(this ShutdownEvidence v)
	{
		return v switch
		{
			ShutdownEvidence.OsShutdown => "os_shutdown", 
			ShutdownEvidence.UnexpectedPower => "unexpected_power", 
			ShutdownEvidence.None => "none", 
			_ => "unknown", 
		};
	}

	public static string ToWire(this AvailabilityState v)
	{
		return v switch
		{
			AvailabilityState.Unavailable => "unavailable", 
			AvailabilityState.Abandoned => "abandoned", 
			_ => "unknown", 
		};
	}

	public static string ToWire(this DiskAction v)
	{
		if (v == DiskAction.BlockedStart)
		{
			return "blocked_start";
		}
		return "unknown";
	}

	public static string ToWire(this RepairOutcome v)
	{
		return v switch
		{
			RepairOutcome.Succeeded => "succeeded", 
			RepairOutcome.Failed => "failed", 
			_ => "unknown", 
		};
	}

	public static string ToWire(this Endpoint v)
	{
		return v switch
		{
			Endpoint.Auth => "auth", 
			Endpoint.Api => "api", 
			Endpoint.Updates => "updates", 
			Endpoint.Storage => "storage", 
			_ => "unknown", 
		};
	}

	public static string ToWire(this BlockReason v)
	{
		return v switch
		{
			BlockReason.UpdateRequired => "update_required", 
			BlockReason.ServerBlocked => "server_blocked", 
			BlockReason.DeviceBanned => "device_banned", 
			BlockReason.Auth => "auth", 
			_ => "unknown", 
		};
	}

	public static string ToWire(this SeatStatus v)
	{
		return v switch
		{
			SeatStatus.Ok => "ok", 
			SeatStatus.InvalidCode => "invalid_code", 
			SeatStatus.NotFound => "not_found", 
			SeatStatus.CodeExpired => "code_expired", 
			SeatStatus.RecoveryTokenRequired => "recovery_token_required", 
			SeatStatus.SecretConflict => "secret_conflict", 
			SeatStatus.NoHardwareUuid => "no_hardware_uuid", 
			SeatStatus.Other => "other", 
			_ => "unknown", 
		};
	}

	public static string ToWire(this UploadFailReason v)
	{
		return v switch
		{
			UploadFailReason.Network => "network", 
			UploadFailReason.Http4xx => "http_4xx", 
			UploadFailReason.Http5xx => "http_5xx", 
			UploadFailReason.Disk => "disk", 
			UploadFailReason.PresignRejected => "presign_rejected", 
			UploadFailReason.DigestMismatch => "digest_mismatch", 
			UploadFailReason.UploadGone => "upload_gone", 
			UploadFailReason.IntegrityHeal => "integrity_heal", 
			UploadFailReason.Other => "other", 
			_ => "unknown", 
		};
	}

	public static string ToWire(this UploadReaction v)
	{
		return v switch
		{
			UploadReaction.PresignRefresh => "presign_refresh", 
			UploadReaction.DigestRehash => "digest_rehash", 
			UploadReaction.ObjectExists => "object_exists", 
			UploadReaction.UploadGone => "upload_gone", 
			UploadReaction.HealMissingObject => "heal_missing_object", 
			UploadReaction.HealSizeMismatch => "heal_size_mismatch", 
			UploadReaction.HealHashMismatch => "heal_hash_mismatch", 
			UploadReaction.HealEscalated => "heal_escalated", 
			UploadReaction.PartsResumed => "parts_resumed", 
			_ => "unknown", 
		};
	}

	public static string ToWire(this AlreadyUploadedOutcome v)
	{
		return v switch
		{
			AlreadyUploadedOutcome.Verified => "verified", 
			AlreadyUploadedOutcome.TrustingDelete => "trusting_delete", 
			AlreadyUploadedOutcome.Collision => "collision", 
			_ => "unknown", 
		};
	}

	public static string ToWire(this CaptureStartFailReason v)
	{
		return v switch
		{
			CaptureStartFailReason.PermissionDenied => "permission_denied", 
			CaptureStartFailReason.LockScreen => "lock_screen", 
			CaptureStartFailReason.BackendUnavailable => "backend_unavailable", 
			CaptureStartFailReason.DisplayChange => "display_change", 
			CaptureStartFailReason.Other => "other", 
			_ => "unknown", 
		};
	}

	public static string ToWire(this UpdateOutcome v)
	{
		return v switch
		{
			UpdateOutcome.Attempt => "attempt", 
			UpdateOutcome.Ok => "ok", 
			UpdateOutcome.Fail => "fail", 
			_ => "unknown", 
		};
	}

	public static string ToWire(this RelocationSource v)
	{
		return v switch
		{
			RelocationSource.DiskImage => "disk_image", 
			RelocationSource.Translocated => "translocated", 
			RelocationSource.OtherFolder => "other_folder", 
			_ => "unknown", 
		};
	}

	public static string ToWire(this RelocationTarget v)
	{
		return v switch
		{
			RelocationTarget.Applications => "applications", 
			RelocationTarget.UserApplications => "user_applications", 
			_ => "unknown", 
		};
	}

	public static string ToWire(this RelocationOutcome v)
	{
		return v switch
		{
			RelocationOutcome.Moved => "moved", 
			RelocationOutcome.OpenedInstalled => "opened_installed", 
			RelocationOutcome.Failed => "failed", 
			_ => "unknown", 
		};
	}

	public static string ToWire(this DnsEvent v)
	{
		return v switch
		{
			DnsEvent.Degraded => "degraded", 
			DnsEvent.Recovered => "recovered", 
			DnsEvent.Exhausted => "exhausted", 
			_ => "unknown", 
		};
	}

	public static string ToWire(this ContainedExceptionSource v)
	{
		return v switch
		{
			ContainedExceptionSource.Dispatcher => "dispatcher", 
			ContainedExceptionSource.Winforms => "winforms", 
			ContainedExceptionSource.UnobservedTask => "unobserved_task", 
			_ => "unknown", 
		};
	}

	public static string ToWire(this UiaProbeEvent v)
	{
		return v switch
		{
			UiaProbeEvent.Ready => "ready", 
			UiaProbeEvent.Lost => "lost", 
			UiaProbeEvent.SpawnFailed => "spawn_failed", 
			UiaProbeEvent.StoodDown => "stood_down", 
			UiaProbeEvent.AppSuppressed => "app_suppressed", 
			UiaProbeEvent.CrashDetected => "crash_detected", 
			_ => "unknown", 
		};
	}

	public static string ToWire(this AxRecordKind v)
	{
		return v switch
		{
			AxRecordKind.Header => "header", 
			AxRecordKind.Key => "key", 
			AxRecordKind.Walk => "walk", 
			AxRecordKind.Note => "note", 
			AxRecordKind.Act => "act", 
			AxRecordKind.Focus => "focus", 
			AxRecordKind.Health => "health", 
			AxRecordKind.Dark => "dark", 
			AxRecordKind.Seal => "seal", 
			AxRecordKind.Gap => "gap", 
			AxRecordKind.Census => "census", 
			AxRecordKind.Px => "px", 
			_ => "unknown", 
		};
	}

	public static string ToWire(this AxPartialReason v)
	{
		return v switch
		{
			AxPartialReason.Time => "time", 
			AxPartialReason.Interaction => "interaction", 
			AxPartialReason.MaxNodes => "max_nodes", 
			AxPartialReason.MaxDepth => "max_depth", 
			AxPartialReason.MaxChildren => "max_children", 
			AxPartialReason.Timeout => "timeout", 
			AxPartialReason.WindowChange => "window_change", 
			AxPartialReason.Budget => "budget", 
			_ => "unknown", 
		};
	}

	public static string ToWire(this AxActStatus v)
	{
		return v switch
		{
			AxActStatus.Ok => "ok", 
			AxActStatus.Refined => "refined", 
			AxActStatus.Late => "late", 
			AxActStatus.Timeout => "timeout", 
			AxActStatus.PidMismatch => "pid_mismatch", 
			AxActStatus.Dark => "dark", 
			AxActStatus.Unmatched => "unmatched", 
			AxActStatus.Inserted => "inserted", 
			_ => "unknown", 
		};
	}

	public static string ToWire(this ContentionMatch v)
	{
		return v switch
		{
			ContentionMatch.FileName => "file_name", 
			ContentionMatch.ProductName => "product_name", 
			ContentionMatch.OriginalFilename => "original_filename", 
			ContentionMatch.InternalName => "internal_name", 
			ContentionMatch.FileDescription => "file_description", 
			ContentionMatch.CompanyName => "company_name", 
			ContentionMatch.Signer => "signer", 
			ContentionMatch.Executable => "executable", 
			ContentionMatch.BundleId => "bundle_id", 
			ContentionMatch.TeamId => "team_id", 
			ContentionMatch.SigningId => "signing_id", 
			_ => "unknown", 
		};
	}

	public static string ToWire(this ContentionEnd v)
	{
		return v switch
		{
			ContentionEnd.Cleared => "cleared", 
			ContentionEnd.RecordingOff => "recording_off", 
			ContentionEnd.SignedOut => "signed_out", 
			ContentionEnd.ListChanged => "list_changed", 
			_ => "unknown", 
		};
	}

	public static string ToWire(this ParkEnd v)
	{
		return v switch
		{
			ParkEnd.Resumed => "resumed", 
			ParkEnd.SessionEnded => "session_ended", 
			_ => "unknown", 
		};
	}

	public static string ToWire(this ResumeRefusal v)
	{
		return v switch
		{
			ResumeRefusal.PrivacyStillActive => "privacy_still_active", 
			ResumeRefusal.UserIdle => "user_idle", 
			ResumeRefusal.SuspendLatch => "suspend_latch", 
			ResumeRefusal.SystemInactive => "system_inactive", 
			ResumeRefusal.SuspendFence => "suspend_fence", 
			ResumeRefusal.SessionNotInteractive => "session_not_interactive", 
			ResumeRefusal.LockShellUnresolved => "lock_shell_unresolved", 
			ResumeRefusal.SegmentActive => "segment_active", 
			_ => "unknown", 
		};
	}

	public static string ToWire(this AxDarkReason v)
	{
		return v switch
		{
			AxDarkReason.NoChildren => "no_children", 
			AxDarkReason.NotTrusted => "not_trusted", 
			AxDarkReason.Excluded => "excluded", 
			AxDarkReason.Suppressed => "suppressed", 
			AxDarkReason.Hostile => "hostile", 
			AxDarkReason.HelperDead => "helper_dead", 
			AxDarkReason.Hung => "hung", 
			AxDarkReason.Elevated => "elevated", 
			AxDarkReason.SecureDesktop => "secure_desktop", 
			AxDarkReason.OwnProcess => "own_process", 
			_ => "unknown", 
		};
	}

	public static string ToWire(this AxHelperEvent v)
	{
		return v switch
		{
			AxHelperEvent.Spawned => "spawned", 
			AxHelperEvent.Died => "died", 
			AxHelperEvent.SpawnFailed => "spawn_failed", 
			AxHelperEvent.StoodDown => "stood_down", 
			AxHelperEvent.Deadline => "deadline", 
			AxHelperEvent.TrustLost => "trust_lost", 
			_ => "unknown", 
		};
	}

	public static string ToWire(this StandbyBucket v)
	{
		return v switch
		{
			StandbyBucket.Exempted => "exempted", 
			StandbyBucket.Active => "active", 
			StandbyBucket.WorkingSet => "working_set", 
			StandbyBucket.Frequent => "frequent", 
			StandbyBucket.Rare => "rare", 
			StandbyBucket.Restricted => "restricted", 
			StandbyBucket.Other => "other", 
			_ => "unknown", 
		};
	}

	public static string ToWire(this AppExitReason v)
	{
		return v switch
		{
			AppExitReason.Crash => "crash", 
			AppExitReason.NativeCrash => "native_crash", 
			AppExitReason.Anr => "anr", 
			AppExitReason.InitFailure => "init_failure", 
			AppExitReason.LowMemory => "low_memory", 
			AppExitReason.ExcessiveResource => "excessive_resource", 
			AppExitReason.Signaled => "signaled", 
			AppExitReason.UserRequested => "user_requested", 
			AppExitReason.UserStopped => "user_stopped", 
			AppExitReason.PackageUpdated => "package_updated", 
			AppExitReason.PackageStateChange => "package_state_change", 
			AppExitReason.PermissionChange => "permission_change", 
			AppExitReason.DependencyDied => "dependency_died", 
			AppExitReason.Freezer => "freezer", 
			AppExitReason.ExitSelf => "exit_self", 
			AppExitReason.Other => "other", 
			_ => "unknown", 
		};
	}

	public static string ToWire(this SetupStep v)
	{
		return v switch
		{
			SetupStep.Floor => "floor", 
			SetupStep.Update => "update", 
			SetupStep.Account => "account", 
			SetupStep.SignIn => "sign_in", 
			SetupStep.Terms => "terms", 
			SetupStep.Notifications => "notifications", 
			SetupStep.DevOptions => "dev_options", 
			SetupStep.Wireless => "wireless", 
			SetupStep.A11y => "a11y", 
			SetupStep.Pair => "pair", 
			SetupStep.OemToggle => "oem_toggle", 
			SetupStep.Register => "register", 
			SetupStep.HelperFailed => "helper_failed", 
			SetupStep.A11yFailed => "a11y_failed", 
			SetupStep.Done => "done", 
			_ => "unknown", 
		};
	}

	public static string ToWire(this AudioState v)
	{
		return v switch
		{
			AudioState.On => "on", 
			AudioState.Failed => "failed", 
			AudioState.Interrupted => "interrupted", 
			AudioState.Off => "off", 
			_ => "unknown", 
		};
	}

	public static string ToWire(this HelperInputPriority v)
	{
		return v switch
		{
			HelperInputPriority.UrgentDisplay => "urgent_display", 
			HelperInputPriority.Refused => "refused", 
			_ => "unknown", 
		};
	}

	public static string ToWire(this TimingQuality v)
	{
		return v switch
		{
			TimingQuality.Authoritative => "authoritative", 
			TimingQuality.Degraded => "degraded", 
			_ => "unknown", 
		};
	}

	public static string ToWire(this CaptureRecoveryLabel v)
	{
		return v switch
		{
			CaptureRecoveryLabel.Attempt1 => "attempt_1", 
			CaptureRecoveryLabel.Attempt23 => "attempt_2_3", 
			CaptureRecoveryLabel.Attempt4Plus => "attempt_4_plus", 
			_ => "unknown", 
		};
	}

	public static string ToWire(this EncoderStallLabel v)
	{
		return v switch
		{
			EncoderStallLabel.Ge250ms => "ge_250ms", 
			EncoderStallLabel.Ge1000ms => "ge_1000ms", 
			_ => "unknown", 
		};
	}

	public static string ToWire(this UpdateCacheRepairLabel v)
	{
		return v switch
		{
			UpdateCacheRepairLabel.MovedAside => "moved_aside", 
			UpdateCacheRepairLabel.Failed => "failed", 
			_ => "unknown", 
		};
	}

	public static string ToWire(this InputNonFiniteLabel v)
	{
		return v switch
		{
			InputNonFiniteLabel.Move => "move", 
			InputNonFiniteLabel.Drag => "drag", 
			InputNonFiniteLabel.Click => "click", 
			InputNonFiniteLabel.Scroll => "scroll", 
			InputNonFiniteLabel.MouseLocation => "mouse_location", 
			_ => "unknown", 
		};
	}
}
