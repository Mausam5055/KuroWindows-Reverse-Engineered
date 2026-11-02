namespace ClientTelemetry;

public static class Count
{
	public static void SessionStart(long by = 1L)
	{
		Telemetry.Count("session_start", null, by);
	}

	public static void SessionCleanStop(long by = 1L)
	{
		Telemetry.Count("session_clean_stop", null, by);
	}

	public static void SessionDirtyStart(long by = 1L)
	{
		Telemetry.Count("session_dirty_start", null, by);
	}

	public static void CaptureStartAttempt(long by = 1L)
	{
		Telemetry.Count("capture_start_attempt", null, by);
	}

	public static void CaptureStartFail(CaptureStartFailReason label, long by = 1L)
	{
		Telemetry.Count("capture_start_fail", label.ToWire(), by);
	}

	public static void CaptureDenied(CaptureDenialClass label, long by = 1L)
	{
		Telemetry.Count("capture_denied", label.ToWire(), by);
	}

	public static void CaptureBackendFallback(CaptureBackend label, long by = 1L)
	{
		Telemetry.Count("capture_backend_fallback", label.ToWire(), by);
	}

	public static void CaptureRecovery(CaptureRecoveryLabel label, long by = 1L)
	{
		Telemetry.Count("capture_recovery", label.ToWire(), by);
	}

	public static void InputHookRearm(long by = 1L)
	{
		Telemetry.Count("input_hook_rearm", null, by);
	}

	public static void RecordingMinutes(long by = 1L)
	{
		Telemetry.Count("recording_minutes", null, by);
	}

	public static void EncoderStall(EncoderStallLabel label, long by = 1L)
	{
		Telemetry.Count("encoder_stall", label.ToWire(), by);
	}

	public static void EncoderThrottleEvents(long by = 1L)
	{
		Telemetry.Count("encoder_throttle_events", null, by);
	}

	public static void SystemAudioLatched(long by = 1L)
	{
		Telemetry.Count("system_audio_latched", null, by);
	}

	public static void MaskTriggers(long by = 1L)
	{
		Telemetry.Count("mask_triggers", null, by);
	}

	public static void SegmentSealed(long by = 1L)
	{
		Telemetry.Count("segment_sealed", null, by);
	}

	public static void SegmentUploadOk(long by = 1L)
	{
		Telemetry.Count("segment_upload_ok", null, by);
	}

	public static void SegmentUploadFail(UploadFailReason label, long by = 1L)
	{
		Telemetry.Count("segment_upload_fail", label.ToWire(), by);
	}

	public static void UploadReaction(UploadReaction label, long by = 1L)
	{
		Telemetry.Count("upload_reaction", label.ToWire(), by);
	}

	public static void AlreadyUploaded(AlreadyUploadedOutcome label, long by = 1L)
	{
		Telemetry.Count("already_uploaded", label.ToWire(), by);
	}

	public static void UploadWakeSeconds(long by = 1L)
	{
		Telemetry.Count("upload_wake_seconds", null, by);
	}

	public static void UploadPauseOpened(long by = 1L)
	{
		Telemetry.Count("upload_pause_opened", null, by);
	}

	public static void AuthRefreshOk(long by = 1L)
	{
		Telemetry.Count("auth_refresh_ok", null, by);
	}

	public static void AuthRefreshFail(AuthFailureReason label, long by = 1L)
	{
		Telemetry.Count("auth_refresh_fail", label.ToWire(), by);
	}

	public static void DiskLow(long by = 1L)
	{
		Telemetry.Count("disk_low", null, by);
	}

	public static void Upgrade(UpdateOutcome label, long by = 1L)
	{
		Telemetry.Count("upgrade", label.ToWire(), by);
	}

	public static void SegmentDeleted(DeleteCause label, long by = 1L)
	{
		Telemetry.Count("segment_deleted", label.ToWire(), by);
	}

	public static void DnsFallback(DnsEvent label, long by = 1L)
	{
		Telemetry.Count("dns_fallback", label.ToWire(), by);
	}

	public static void UpdateCacheRepair(UpdateCacheRepairLabel label, long by = 1L)
	{
		Telemetry.Count("update_cache_repair", label.ToWire(), by);
	}

	public static void InputNonFinite(InputNonFiniteLabel label, long by = 1L)
	{
		Telemetry.Count("input_non_finite", label.ToWire(), by);
	}

	public static void LocalLogWriteFailed(long by = 1L)
	{
		Telemetry.Count("local_log_write_failed", null, by);
	}

	public static void ContainedException(ContainedExceptionSource label, long by = 1L)
	{
		Telemetry.Count("contained_exception", label.ToWire(), by);
	}

	public static void AppExit(AppExitReason label, long by = 1L)
	{
		Telemetry.Count("app_exit", label.ToWire(), by);
	}

	public static void UiaProbe(UiaProbeEvent label, long by = 1L)
	{
		Telemetry.Count("uia_probe", label.ToWire(), by);
	}

	public static void AxRecords(AxRecordKind label, long by = 1L)
	{
		Telemetry.Count("ax_records", label.ToWire(), by);
	}

	public static void AxWalksPartial(AxPartialReason label, long by = 1L)
	{
		Telemetry.Count("ax_walks_partial", label.ToWire(), by);
	}

	public static void AxActions(AxActStatus label, long by = 1L)
	{
		Telemetry.Count("ax_actions", label.ToWire(), by);
	}

	public static void AxDarkSeconds(AxDarkReason label, long by = 1L)
	{
		Telemetry.Count("ax_dark_seconds", label.ToWire(), by);
	}

	public static void AxHelper(AxHelperEvent label, long by = 1L)
	{
		Telemetry.Count("ax_helper", label.ToWire(), by);
	}
}
