namespace ClientTelemetry;

public static class Ev
{
	public static TelemetryEvent SessionStarted(string recordingId, long? displayCount = null, bool? batteryExempt = null, bool? powerSave = null, StandbyBucket? standbyBucket = null, bool? backgroundRestricted = null)
	{
		Attributes attributes = new Attributes(6);
		attributes.AddIdentifier("recording_id", recordingId);
		if (displayCount.HasValue)
		{
			long valueOrDefault = displayCount.GetValueOrDefault();
			attributes.Add("display_count", valueOrDefault);
		}
		if (batteryExempt.HasValue)
		{
			bool valueOrDefault2 = batteryExempt == true;
			attributes.Add("battery_exempt", valueOrDefault2);
		}
		if (powerSave.HasValue)
		{
			bool valueOrDefault3 = powerSave == true;
			attributes.Add("power_save", valueOrDefault3);
		}
		if (standbyBucket.HasValue)
		{
			StandbyBucket valueOrDefault4 = standbyBucket.GetValueOrDefault();
			attributes.Add("standby_bucket", valueOrDefault4.ToWire());
		}
		if (backgroundRestricted.HasValue)
		{
			bool valueOrDefault5 = backgroundRestricted == true;
			attributes.Add("background_restricted", valueOrDefault5);
		}
		return new TelemetryEvent("session.started", Kind.Wide, Severity.Info, Gate.Standard, attributes);
	}

	public static TelemetryEvent SessionEnded(string recordingId, SessionEndReason endReason)
	{
		Attributes attributes = new Attributes(2);
		attributes.AddIdentifier("recording_id", recordingId);
		attributes.Add("end_reason", endReason.ToWire());
		return new TelemetryEvent("session.ended", Kind.Wide, Severity.Info, Gate.Standard, attributes);
	}

	public static TelemetryEvent SessionParked(string recordingId, string reason, long heldMs, bool atStart, ParkEnd endedBy, string? heldBy = null, long? refusals = null, ResumeRefusal? refused = null)
	{
		Attributes attributes = new Attributes();
		attributes.AddIdentifier("recording_id", recordingId);
		attributes.AddIdentifier("reason", reason);
		attributes.Add("held_ms", heldMs);
		attributes.Add("at_start", atStart);
		attributes.Add("ended_by", endedBy.ToWire());
		if (heldBy != null)
		{
			attributes.AddIdentifier("held_by", heldBy);
		}
		if (refusals.HasValue)
		{
			long valueOrDefault = refusals.GetValueOrDefault();
			attributes.Add("refusals", valueOrDefault);
		}
		if (refused.HasValue)
		{
			ResumeRefusal valueOrDefault2 = refused.GetValueOrDefault();
			attributes.Add("refused", valueOrDefault2.ToWire());
		}
		return new TelemetryEvent("session.parked", Kind.Signal, Severity.Info, Gate.Standard, attributes);
	}

	public static TelemetryEvent CaptureContentionStarted(ContentionMatch match, string matchHash, bool? inFront = null)
	{
		Attributes attributes = new Attributes(3);
		attributes.Add("match", match.ToWire());
		attributes.AddIdentifier("match_hash", matchHash);
		if (inFront.HasValue)
		{
			bool valueOrDefault = inFront == true;
			attributes.Add("in_front", valueOrDefault);
		}
		return new TelemetryEvent("capture.contention_started", Kind.Signal, Severity.Info, Gate.Standard, attributes);
	}

	public static TelemetryEvent CaptureContentionEnded(ContentionEnd reason, long heldMs)
	{
		Attributes attributes = new Attributes(2);
		attributes.Add("reason", reason.ToWire());
		attributes.Add("held_ms", heldMs);
		return new TelemetryEvent("capture.contention_ended", Kind.Signal, Severity.Info, Gate.Standard, attributes);
	}

	public static TelemetryEvent SessionStartFailed(SessionStartFailure reason, string? errorCode = null, string? errorMessage = null)
	{
		Attributes attributes = new Attributes(3);
		attributes.Add("reason", reason.ToWire());
		if (errorCode != null)
		{
			attributes.AddIdentifier("error_code", errorCode);
		}
		if (errorMessage != null)
		{
			attributes.AddText("error_message", errorMessage, 256);
		}
		return new TelemetryEvent("session.start_failed", Kind.Signal, Severity.Error, Gate.Required, attributes);
	}

	public static TelemetryEvent SegmentSealed(string recordingId, long segmentIndex, SegmentDisposition disposition, long durationMs, long? maskTriggers = null, long? displayCount = null, FateOrigin? origin = null, string? deviceModel = null, long? thermalMax = null, long? thermalThrottledMs = null, double? thermalHeadroomMax = null, double? appCpuShare = null, double? helperCpuShare = null, long? appMemoryMb = null, long? helperMemoryMb = null, long? lowMemoryEvents = null, long? frames = null, long? framesDropped = null, long? maxGapMs = null, long? encoderResets = null, long? uiResponseP95Ms = null, long? batteryStart = null, long? batteryEnd = null, long? chargingMs = null, AudioState? audioState = null, string? audioReason = null, RecordingCore? recorderCore = null, CaptureSource? captureSource = null, EncoderKind? encoder = null, long? freshFrames = null, long? audioZerosMs = null, long? audioDroppedMs = null, long? audioCorrections = null, long? audioMaxErrorMs = null, long? audioDriftPpm = null, long? audioSilenceMs = null, HelperInputPriority? helperInputPriority = null, long? samplesDroppedChain = null, TimingQuality? timingQuality = null, long? appFrozenMs = null)
	{
		Attributes attributes = new Attributes(40);
		attributes.AddIdentifier("recording_id", recordingId);
		attributes.Add("segment_index", segmentIndex);
		attributes.Add("disposition", disposition.ToWire());
		attributes.Add("duration_ms", durationMs);
		if (maskTriggers.HasValue)
		{
			long valueOrDefault = maskTriggers.GetValueOrDefault();
			attributes.Add("mask_triggers", valueOrDefault);
		}
		if (displayCount.HasValue)
		{
			long valueOrDefault2 = displayCount.GetValueOrDefault();
			attributes.Add("display_count", valueOrDefault2);
		}
		if (origin.HasValue)
		{
			FateOrigin valueOrDefault3 = origin.GetValueOrDefault();
			attributes.Add("origin", valueOrDefault3.ToWire());
		}
		if (deviceModel != null)
		{
			attributes.AddIdentifier("device_model", deviceModel);
		}
		if (thermalMax.HasValue)
		{
			long valueOrDefault4 = thermalMax.GetValueOrDefault();
			attributes.Add("thermal_max", valueOrDefault4);
		}
		if (thermalThrottledMs.HasValue)
		{
			long valueOrDefault5 = thermalThrottledMs.GetValueOrDefault();
			attributes.Add("thermal_throttled_ms", valueOrDefault5);
		}
		if (thermalHeadroomMax.HasValue)
		{
			double valueOrDefault6 = thermalHeadroomMax.GetValueOrDefault();
			attributes.Add("thermal_headroom_max", valueOrDefault6);
		}
		if (appCpuShare.HasValue)
		{
			double valueOrDefault7 = appCpuShare.GetValueOrDefault();
			attributes.Add("app_cpu_share", valueOrDefault7);
		}
		if (helperCpuShare.HasValue)
		{
			double valueOrDefault8 = helperCpuShare.GetValueOrDefault();
			attributes.Add("helper_cpu_share", valueOrDefault8);
		}
		if (appMemoryMb.HasValue)
		{
			long valueOrDefault9 = appMemoryMb.GetValueOrDefault();
			attributes.Add("app_memory_mb", valueOrDefault9);
		}
		if (helperMemoryMb.HasValue)
		{
			long valueOrDefault10 = helperMemoryMb.GetValueOrDefault();
			attributes.Add("helper_memory_mb", valueOrDefault10);
		}
		if (lowMemoryEvents.HasValue)
		{
			long valueOrDefault11 = lowMemoryEvents.GetValueOrDefault();
			attributes.Add("low_memory_events", valueOrDefault11);
		}
		if (frames.HasValue)
		{
			long valueOrDefault12 = frames.GetValueOrDefault();
			attributes.Add("frames", valueOrDefault12);
		}
		if (framesDropped.HasValue)
		{
			long valueOrDefault13 = framesDropped.GetValueOrDefault();
			attributes.Add("frames_dropped", valueOrDefault13);
		}
		if (maxGapMs.HasValue)
		{
			long valueOrDefault14 = maxGapMs.GetValueOrDefault();
			attributes.Add("max_gap_ms", valueOrDefault14);
		}
		if (encoderResets.HasValue)
		{
			long valueOrDefault15 = encoderResets.GetValueOrDefault();
			attributes.Add("encoder_resets", valueOrDefault15);
		}
		if (uiResponseP95Ms.HasValue)
		{
			long valueOrDefault16 = uiResponseP95Ms.GetValueOrDefault();
			attributes.Add("ui_response_p95_ms", valueOrDefault16);
		}
		if (batteryStart.HasValue)
		{
			long valueOrDefault17 = batteryStart.GetValueOrDefault();
			attributes.Add("battery_start", valueOrDefault17);
		}
		if (batteryEnd.HasValue)
		{
			long valueOrDefault18 = batteryEnd.GetValueOrDefault();
			attributes.Add("battery_end", valueOrDefault18);
		}
		if (chargingMs.HasValue)
		{
			long valueOrDefault19 = chargingMs.GetValueOrDefault();
			attributes.Add("charging_ms", valueOrDefault19);
		}
		if (audioState.HasValue)
		{
			AudioState valueOrDefault20 = audioState.GetValueOrDefault();
			attributes.Add("audio_state", valueOrDefault20.ToWire());
		}
		if (audioReason != null)
		{
			attributes.AddIdentifier("audio_reason", audioReason);
		}
		if (recorderCore.HasValue)
		{
			RecordingCore valueOrDefault21 = recorderCore.GetValueOrDefault();
			attributes.Add("recorder_core", valueOrDefault21.ToWire());
		}
		if (captureSource.HasValue)
		{
			CaptureSource valueOrDefault22 = captureSource.GetValueOrDefault();
			attributes.Add("capture_source", valueOrDefault22.ToWire());
		}
		if (encoder.HasValue)
		{
			EncoderKind valueOrDefault23 = encoder.GetValueOrDefault();
			attributes.Add("encoder", valueOrDefault23.ToWire());
		}
		if (freshFrames.HasValue)
		{
			long valueOrDefault24 = freshFrames.GetValueOrDefault();
			attributes.Add("fresh_frames", valueOrDefault24);
		}
		if (audioZerosMs.HasValue)
		{
			long valueOrDefault25 = audioZerosMs.GetValueOrDefault();
			attributes.Add("audio_zeros_ms", valueOrDefault25);
		}
		if (audioDroppedMs.HasValue)
		{
			long valueOrDefault26 = audioDroppedMs.GetValueOrDefault();
			attributes.Add("audio_dropped_ms", valueOrDefault26);
		}
		if (audioCorrections.HasValue)
		{
			long valueOrDefault27 = audioCorrections.GetValueOrDefault();
			attributes.Add("audio_corrections", valueOrDefault27);
		}
		if (audioMaxErrorMs.HasValue)
		{
			long valueOrDefault28 = audioMaxErrorMs.GetValueOrDefault();
			attributes.Add("audio_max_error_ms", valueOrDefault28);
		}
		if (audioDriftPpm.HasValue)
		{
			long valueOrDefault29 = audioDriftPpm.GetValueOrDefault();
			attributes.Add("audio_drift_ppm", valueOrDefault29);
		}
		if (audioSilenceMs.HasValue)
		{
			long valueOrDefault30 = audioSilenceMs.GetValueOrDefault();
			attributes.Add("audio_silence_ms", valueOrDefault30);
		}
		if (helperInputPriority.HasValue)
		{
			HelperInputPriority valueOrDefault31 = helperInputPriority.GetValueOrDefault();
			attributes.Add("helper_input_priority", valueOrDefault31.ToWire());
		}
		if (samplesDroppedChain.HasValue)
		{
			long valueOrDefault32 = samplesDroppedChain.GetValueOrDefault();
			attributes.Add("samples_dropped_chain", valueOrDefault32);
		}
		if (timingQuality.HasValue)
		{
			TimingQuality valueOrDefault33 = timingQuality.GetValueOrDefault();
			attributes.Add("timing_quality", valueOrDefault33.ToWire());
		}
		if (appFrozenMs.HasValue)
		{
			long valueOrDefault34 = appFrozenMs.GetValueOrDefault();
			attributes.Add("app_frozen_ms", valueOrDefault34);
		}
		return new TelemetryEvent("segment.sealed", Kind.Wide, Severity.Info, Gate.Standard, attributes);
	}

	public static TelemetryEvent SegmentQuarantined(string recordingId, long segmentIndex, string reason, FateOrigin? origin = null, string? detail = null, long? attempts = null)
	{
		Attributes attributes = new Attributes(6);
		attributes.AddIdentifier("recording_id", recordingId);
		attributes.Add("segment_index", segmentIndex);
		attributes.AddIdentifier("reason", reason);
		if (origin.HasValue)
		{
			FateOrigin valueOrDefault = origin.GetValueOrDefault();
			attributes.Add("origin", valueOrDefault.ToWire());
		}
		if (detail != null)
		{
			attributes.AddText("detail", detail, 256);
		}
		if (attempts.HasValue)
		{
			long valueOrDefault2 = attempts.GetValueOrDefault();
			attributes.Add("attempts", valueOrDefault2);
		}
		return new TelemetryEvent("segment.quarantined", Kind.Wide, Severity.Info, Gate.Required, attributes);
	}

	public static TelemetryEvent SegmentDeleted(string recordingId, DeleteCause cause, long? segmentIndex = null, QuarantineReason? quarantineReason = null, SegmentDisposition? disposition = null, long? attempts = null, string? lastErrorCode = null, double? waitedHours = null, long? bytes = null, string? detail = null)
	{
		Attributes attributes = new Attributes(10);
		attributes.AddIdentifier("recording_id", recordingId);
		attributes.Add("cause", cause.ToWire());
		if (segmentIndex.HasValue)
		{
			long valueOrDefault = segmentIndex.GetValueOrDefault();
			attributes.Add("segment_index", valueOrDefault);
		}
		if (quarantineReason.HasValue)
		{
			QuarantineReason valueOrDefault2 = quarantineReason.GetValueOrDefault();
			attributes.Add("quarantine_reason", valueOrDefault2.ToWire());
		}
		if (disposition.HasValue)
		{
			SegmentDisposition valueOrDefault3 = disposition.GetValueOrDefault();
			attributes.Add("disposition", valueOrDefault3.ToWire());
		}
		if (attempts.HasValue)
		{
			long valueOrDefault4 = attempts.GetValueOrDefault();
			attributes.Add("attempts", valueOrDefault4);
		}
		if (lastErrorCode != null)
		{
			attributes.AddIdentifier("last_error_code", lastErrorCode);
		}
		if (waitedHours.HasValue)
		{
			double valueOrDefault5 = waitedHours.GetValueOrDefault();
			attributes.Add("waited_hours", valueOrDefault5);
		}
		if (bytes.HasValue)
		{
			long valueOrDefault6 = bytes.GetValueOrDefault();
			attributes.Add("bytes", valueOrDefault6);
		}
		if (detail != null)
		{
			attributes.AddText("detail", detail, 256);
		}
		return new TelemetryEvent("segment.deleted", Kind.Wide, Severity.Info, Gate.Required, attributes);
	}

	public static TelemetryEvent SegmentSuspect(string recordingId, long segmentIndex, string reason)
	{
		Attributes attributes = new Attributes(3);
		attributes.AddIdentifier("recording_id", recordingId);
		attributes.Add("segment_index", segmentIndex);
		attributes.AddIdentifier("reason", reason);
		return new TelemetryEvent("segment.suspect", Kind.Signal, Severity.Warn, Gate.Standard, attributes);
	}

	public static TelemetryEvent UploadFinished(string recordingId, long segmentIndex, long attempts, long? files = null, long? bytes = null, long? elapsedMs = null, long? requestMs = null, long? putMs = null, long? completeMs = null, bool? multipart = null, string? serverStatus = null, long? wakeMs = null)
	{
		Attributes attributes = new Attributes(12);
		attributes.AddIdentifier("recording_id", recordingId);
		attributes.Add("segment_index", segmentIndex);
		attributes.Add("attempts", attempts);
		if (files.HasValue)
		{
			long valueOrDefault = files.GetValueOrDefault();
			attributes.Add("files", valueOrDefault);
		}
		if (bytes.HasValue)
		{
			long valueOrDefault2 = bytes.GetValueOrDefault();
			attributes.Add("bytes", valueOrDefault2);
		}
		if (elapsedMs.HasValue)
		{
			long valueOrDefault3 = elapsedMs.GetValueOrDefault();
			attributes.Add("elapsed_ms", valueOrDefault3);
		}
		if (requestMs.HasValue)
		{
			long valueOrDefault4 = requestMs.GetValueOrDefault();
			attributes.Add("request_ms", valueOrDefault4);
		}
		if (putMs.HasValue)
		{
			long valueOrDefault5 = putMs.GetValueOrDefault();
			attributes.Add("put_ms", valueOrDefault5);
		}
		if (completeMs.HasValue)
		{
			long valueOrDefault6 = completeMs.GetValueOrDefault();
			attributes.Add("complete_ms", valueOrDefault6);
		}
		if (multipart.HasValue)
		{
			bool valueOrDefault7 = multipart == true;
			attributes.Add("multipart", valueOrDefault7);
		}
		if (serverStatus != null)
		{
			attributes.AddIdentifier("server_status", serverStatus);
		}
		if (wakeMs.HasValue)
		{
			long valueOrDefault8 = wakeMs.GetValueOrDefault();
			attributes.Add("wake_ms", valueOrDefault8);
		}
		return new TelemetryEvent("upload.finished", Kind.Wide, Severity.Info, Gate.Standard, attributes);
	}

	public static TelemetryEvent UploadFailed(string recordingId, long segmentIndex, long attempt, UploadPhase phase, ErrorKind errorKind, long? httpStatus = null, string? errorCode = null, string? errorMessage = null, long? retryInS = null)
	{
		Attributes attributes = new Attributes(9);
		attributes.AddIdentifier("recording_id", recordingId);
		attributes.Add("segment_index", segmentIndex);
		attributes.Add("attempt", attempt);
		attributes.Add("phase", phase.ToWire());
		attributes.Add("error_kind", errorKind.ToWire());
		if (httpStatus.HasValue)
		{
			long valueOrDefault = httpStatus.GetValueOrDefault();
			attributes.Add("http_status", valueOrDefault);
		}
		if (errorCode != null)
		{
			attributes.AddIdentifier("error_code", errorCode);
		}
		if (errorMessage != null)
		{
			attributes.AddText("error_message", errorMessage, 256);
		}
		if (retryInS.HasValue)
		{
			long valueOrDefault2 = retryInS.GetValueOrDefault();
			attributes.Add("retry_in_s", valueOrDefault2);
		}
		return new TelemetryEvent("upload.failed", Kind.Signal, Severity.Warn, Gate.Required, attributes);
	}

	public static TelemetryEvent UploadBlocked(BlockReason reason, string? recordingId = null, long? segmentIndex = null, long? httpStatus = null)
	{
		Attributes attributes = new Attributes(4);
		attributes.Add("reason", reason.ToWire());
		if (recordingId != null)
		{
			attributes.AddIdentifier("recording_id", recordingId);
		}
		if (segmentIndex.HasValue)
		{
			long valueOrDefault = segmentIndex.GetValueOrDefault();
			attributes.Add("segment_index", valueOrDefault);
		}
		if (httpStatus.HasValue)
		{
			long valueOrDefault2 = httpStatus.GetValueOrDefault();
			attributes.Add("http_status", valueOrDefault2);
		}
		return new TelemetryEvent("upload.blocked", Kind.Signal, Severity.Warn, Gate.Required, attributes);
	}

	public static TelemetryEvent UploadIntegrityRejected(string recordingId, long segmentIndex, string fileName, UploadPhase phase, long attempt, long? httpStatus = null, bool? rehashChanged = null, string? errorMessage = null)
	{
		Attributes attributes = new Attributes();
		attributes.AddIdentifier("recording_id", recordingId);
		attributes.Add("segment_index", segmentIndex);
		attributes.AddIdentifier("file_name", fileName);
		attributes.Add("phase", phase.ToWire());
		attributes.Add("attempt", attempt);
		if (httpStatus.HasValue)
		{
			long valueOrDefault = httpStatus.GetValueOrDefault();
			attributes.Add("http_status", valueOrDefault);
		}
		if (rehashChanged.HasValue)
		{
			bool valueOrDefault2 = rehashChanged == true;
			attributes.Add("rehash_changed", valueOrDefault2);
		}
		if (errorMessage != null)
		{
			attributes.AddText("error_message", errorMessage, 256);
		}
		return new TelemetryEvent("upload.integrity_rejected", Kind.Signal, Severity.Warn, Gate.Required, attributes);
	}

	public static TelemetryEvent AuthRefreshFailed(AuthFailureReason reason, long? httpStatus = null, bool? transient = null)
	{
		Attributes attributes = new Attributes(3);
		attributes.Add("reason", reason.ToWire());
		if (httpStatus.HasValue)
		{
			long valueOrDefault = httpStatus.GetValueOrDefault();
			attributes.Add("http_status", valueOrDefault);
		}
		if (transient.HasValue)
		{
			bool valueOrDefault2 = transient == true;
			attributes.Add("transient", valueOrDefault2);
		}
		return new TelemetryEvent("auth.refresh_failed", Kind.Signal, Severity.Warn, Gate.Required, attributes);
	}

	public static TelemetryEvent AuthForcedSignout(SignoutTrigger trigger, long? httpStatus = null)
	{
		Attributes attributes = new Attributes(2);
		attributes.Add("trigger", trigger.ToWire());
		if (httpStatus.HasValue)
		{
			long valueOrDefault = httpStatus.GetValueOrDefault();
			attributes.Add("http_status", valueOrDefault);
		}
		return new TelemetryEvent("auth.forced_signout", Kind.Signal, Severity.Error, Gate.Required, attributes);
	}

	public static TelemetryEvent AuthStateChanged(AuthState to)
	{
		Attributes attributes = new Attributes(1);
		attributes.Add("to", to.ToWire());
		return new TelemetryEvent("auth.state_changed", Kind.Signal, Severity.Info, Gate.Standard, attributes);
	}

	public static TelemetryEvent CaptureStartFailed(long? displayIndex = null, CaptureBackend? backend = null, string? errorCode = null, string? errorMessage = null)
	{
		Attributes attributes = new Attributes(4);
		if (displayIndex.HasValue)
		{
			long valueOrDefault = displayIndex.GetValueOrDefault();
			attributes.Add("display_index", valueOrDefault);
		}
		if (backend.HasValue)
		{
			CaptureBackend valueOrDefault2 = backend.GetValueOrDefault();
			attributes.Add("backend", valueOrDefault2.ToWire());
		}
		if (errorCode != null)
		{
			attributes.AddIdentifier("error_code", errorCode);
		}
		if (errorMessage != null)
		{
			attributes.AddText("error_message", errorMessage, 256);
		}
		return new TelemetryEvent("capture.start_failed", Kind.Signal, Severity.Error, Gate.Required, attributes);
	}

	public static TelemetryEvent CaptureRecoveryScheduled(string failureClass, long attempt, long? delayS = null, string? gateState = null)
	{
		Attributes attributes = new Attributes(4);
		attributes.AddIdentifier("failure_class", failureClass);
		attributes.Add("attempt", attempt);
		if (delayS.HasValue)
		{
			long valueOrDefault = delayS.GetValueOrDefault();
			attributes.Add("delay_s", valueOrDefault);
		}
		if (gateState != null)
		{
			attributes.AddIdentifier("gate_state", gateState);
		}
		return new TelemetryEvent("capture.recovery_scheduled", Kind.Signal, Severity.Warn, Gate.Standard, attributes);
	}

	public static TelemetryEvent CaptureRecovered(long attempts, long? unavailableMs = null)
	{
		Attributes attributes = new Attributes(2);
		attributes.Add("attempts", attempts);
		if (unavailableMs.HasValue)
		{
			long valueOrDefault = unavailableMs.GetValueOrDefault();
			attributes.Add("unavailable_ms", valueOrDefault);
		}
		return new TelemetryEvent("capture.recovered", Kind.Signal, Severity.Info, Gate.Standard, attributes);
	}

	public static TelemetryEvent CaptureAvailabilityChanged(AvailabilityState state, string? failureClass = null)
	{
		Attributes attributes = new Attributes(2);
		attributes.Add("state", state.ToWire());
		if (failureClass != null)
		{
			attributes.AddIdentifier("failure_class", failureClass);
		}
		return new TelemetryEvent("capture.availability_changed", Kind.Signal, Severity.Warn, Gate.Required, attributes);
	}

	public static TelemetryEvent EncoderStall(long elapsedMs, string? recordingId = null, long? segmentIndex = null, long? displayIndex = null, bool? sealedSuspect = null)
	{
		Attributes attributes = new Attributes(5);
		attributes.Add("elapsed_ms", elapsedMs);
		if (recordingId != null)
		{
			attributes.AddIdentifier("recording_id", recordingId);
		}
		if (segmentIndex.HasValue)
		{
			long valueOrDefault = segmentIndex.GetValueOrDefault();
			attributes.Add("segment_index", valueOrDefault);
		}
		if (displayIndex.HasValue)
		{
			long valueOrDefault2 = displayIndex.GetValueOrDefault();
			attributes.Add("display_index", valueOrDefault2);
		}
		if (sealedSuspect.HasValue)
		{
			bool valueOrDefault3 = sealedSuspect == true;
			attributes.Add("sealed_suspect", valueOrDefault3);
		}
		return new TelemetryEvent("encoder.stall", Kind.Signal, Severity.Warn, Gate.Standard, attributes);
	}

	public static TelemetryEvent UiaStoodDown(string reason)
	{
		Attributes attributes = new Attributes(1);
		attributes.AddIdentifier("reason", reason);
		return new TelemetryEvent("uia.stood_down", Kind.Signal, Severity.Warn, Gate.Required, attributes);
	}

	public static TelemetryEvent DiskLow(long availableBytes, long thresholdBytes, DiskAction action)
	{
		Attributes attributes = new Attributes(3);
		attributes.Add("available_bytes", availableBytes);
		attributes.Add("threshold_bytes", thresholdBytes);
		attributes.Add("action", action.ToWire());
		return new TelemetryEvent("disk.low", Kind.Signal, Severity.Warn, Gate.Required, attributes);
	}

	public static TelemetryEvent UpdateStaged(string toVersion, long? bytes = null, bool? delta = null, long? durationMs = null, UpdateTrigger? trigger = null)
	{
		Attributes attributes = new Attributes(5);
		attributes.Add("to_version", toVersion);
		if (bytes.HasValue)
		{
			long valueOrDefault = bytes.GetValueOrDefault();
			attributes.Add("bytes", valueOrDefault);
		}
		if (delta.HasValue)
		{
			bool valueOrDefault2 = delta == true;
			attributes.Add("delta", valueOrDefault2);
		}
		if (durationMs.HasValue)
		{
			long valueOrDefault3 = durationMs.GetValueOrDefault();
			attributes.Add("duration_ms", valueOrDefault3);
		}
		if (trigger.HasValue)
		{
			UpdateTrigger valueOrDefault4 = trigger.GetValueOrDefault();
			attributes.Add("trigger", valueOrDefault4.ToWire());
		}
		return new TelemetryEvent("update.staged", Kind.Signal, Severity.Info, Gate.Required, attributes);
	}

	public static TelemetryEvent UpdateApplied(string toVersion, string? fromVersion = null, UpdateTrigger? trigger = null, long? waitedS = null)
	{
		Attributes attributes = new Attributes(4);
		attributes.Add("to_version", toVersion);
		if (fromVersion != null)
		{
			attributes.Add("from_version", fromVersion);
		}
		if (trigger.HasValue)
		{
			UpdateTrigger valueOrDefault = trigger.GetValueOrDefault();
			attributes.Add("trigger", valueOrDefault.ToWire());
		}
		if (waitedS.HasValue)
		{
			long valueOrDefault2 = waitedS.GetValueOrDefault();
			attributes.Add("waited_s", valueOrDefault2);
		}
		return new TelemetryEvent("update.applied", Kind.Signal, Severity.Info, Gate.Required, attributes);
	}

	public static TelemetryEvent UpdateFailed(UpdateStage stage, string? errorCode = null, string? errorMessage = null, UpdateTrigger? trigger = null, long? attempt = null, string? sparkleCache = null)
	{
		Attributes attributes = new Attributes(6);
		attributes.Add("stage", stage.ToWire());
		if (errorCode != null)
		{
			attributes.AddIdentifier("error_code", errorCode);
		}
		if (errorMessage != null)
		{
			attributes.AddText("error_message", errorMessage, 256);
		}
		if (trigger.HasValue)
		{
			UpdateTrigger valueOrDefault = trigger.GetValueOrDefault();
			attributes.Add("trigger", valueOrDefault.ToWire());
		}
		if (attempt.HasValue)
		{
			long valueOrDefault2 = attempt.GetValueOrDefault();
			attributes.Add("attempt", valueOrDefault2);
		}
		if (sparkleCache != null)
		{
			attributes.AddText("sparkle_cache", sparkleCache, 160);
		}
		return new TelemetryEvent("update.failed", Kind.Signal, Severity.Warn, Gate.Required, attributes);
	}

	public static TelemetryEvent UpdateRequired(string route, string? minVersion = null)
	{
		Attributes attributes = new Attributes(2);
		attributes.AddIdentifier("route", route);
		if (minVersion != null)
		{
			attributes.Add("min_version", minVersion);
		}
		return new TelemetryEvent("update.required", Kind.Signal, Severity.Info, Gate.Required, attributes);
	}

	public static TelemetryEvent UpdateRelocated(RelocationSource fromKind, RelocationOutcome outcome, RelocationTarget? toKind = null, string? existingVersion = null, string? errorMessage = null)
	{
		Attributes attributes = new Attributes(5);
		attributes.Add("from_kind", fromKind.ToWire());
		attributes.Add("outcome", outcome.ToWire());
		if (toKind.HasValue)
		{
			RelocationTarget valueOrDefault = toKind.GetValueOrDefault();
			attributes.Add("to_kind", valueOrDefault.ToWire());
		}
		if (existingVersion != null)
		{
			attributes.Add("existing_version", existingVersion);
		}
		if (errorMessage != null)
		{
			attributes.AddText("error_message", errorMessage, 256);
		}
		return new TelemetryEvent("update.relocated", Kind.Signal, Severity.Info, Gate.Required, attributes);
	}

	public static TelemetryEvent AppCrashed(CrashKind crashKind, string? exceptionType = null, string? signalName = null, string? faultModule = null, string? priorRunId = null, string? milestone = null, long? uptimeS = null, string? breadcrumbs = null, ShutdownEvidence? shutdownEvidence = null, string? bugcheck = null, bool? activeSession = null, long? segmentIndex = null, string? faultOffset = null, string? appOffset = null, string? binaryUuid = null, string? incidentId = null, string? crashQueue = null, CrashStackSource? stackSource = null, long? exitSignal = null, string? stack = null, string? trail = null)
	{
		Attributes attributes = new Attributes(21);
		attributes.Add("crash_kind", crashKind.ToWire());
		if (exceptionType != null)
		{
			attributes.AddIdentifier("exception_type", exceptionType);
		}
		if (signalName != null)
		{
			attributes.AddIdentifier("signal_name", signalName);
		}
		if (faultModule != null)
		{
			attributes.AddIdentifier("fault_module", faultModule);
		}
		if (priorRunId != null)
		{
			attributes.AddIdentifier("prior_run_id", priorRunId);
		}
		if (milestone != null)
		{
			attributes.AddIdentifier("milestone", milestone);
		}
		if (uptimeS.HasValue)
		{
			long valueOrDefault = uptimeS.GetValueOrDefault();
			attributes.Add("uptime_s", valueOrDefault);
		}
		if (breadcrumbs != null)
		{
			attributes.AddText("breadcrumbs", breadcrumbs, 8192);
		}
		if (shutdownEvidence.HasValue)
		{
			ShutdownEvidence valueOrDefault2 = shutdownEvidence.GetValueOrDefault();
			attributes.Add("shutdown_evidence", valueOrDefault2.ToWire());
		}
		if (bugcheck != null)
		{
			attributes.AddIdentifier("bugcheck", bugcheck);
		}
		if (activeSession.HasValue)
		{
			bool valueOrDefault3 = activeSession == true;
			attributes.Add("active_session", valueOrDefault3);
		}
		if (segmentIndex.HasValue)
		{
			long valueOrDefault4 = segmentIndex.GetValueOrDefault();
			attributes.Add("segment_index", valueOrDefault4);
		}
		if (faultOffset != null)
		{
			attributes.AddIdentifier("fault_offset", faultOffset);
		}
		if (appOffset != null)
		{
			attributes.AddIdentifier("app_offset", appOffset);
		}
		if (binaryUuid != null)
		{
			attributes.AddIdentifier("binary_uuid", binaryUuid);
		}
		if (incidentId != null)
		{
			attributes.AddIdentifier("incident_id", incidentId);
		}
		if (crashQueue != null)
		{
			attributes.AddIdentifier("crash_queue", crashQueue);
		}
		if (stackSource.HasValue)
		{
			CrashStackSource valueOrDefault5 = stackSource.GetValueOrDefault();
			attributes.Add("stack_source", valueOrDefault5.ToWire());
		}
		if (exitSignal.HasValue)
		{
			long valueOrDefault6 = exitSignal.GetValueOrDefault();
			attributes.Add("exit_signal", valueOrDefault6);
		}
		if (stack != null)
		{
			attributes.AddText("stack", stack, 3072);
		}
		if (trail != null)
		{
			attributes.AddText("trail", trail, 3072);
		}
		return new TelemetryEvent("app.crashed", Kind.Signal, Severity.Error, Gate.Required, attributes);
	}

	public static TelemetryEvent AppSafeMode(long crashExits, bool afterRetry)
	{
		Attributes attributes = new Attributes(2);
		attributes.Add("crash_exits", crashExits);
		attributes.Add("after_retry", afterRetry);
		return new TelemetryEvent("app.safe_mode", Kind.Signal, Severity.Warn, Gate.Required, attributes);
	}

	public static TelemetryEvent SetupStep(SetupStep step, SetupStep? previous = null, long? previousMs = null)
	{
		Attributes attributes = new Attributes(3);
		attributes.Add("step", step.ToWire());
		if (previous.HasValue)
		{
			SetupStep valueOrDefault = previous.GetValueOrDefault();
			attributes.Add("previous", valueOrDefault.ToWire());
		}
		if (previousMs.HasValue)
		{
			long valueOrDefault2 = previousMs.GetValueOrDefault();
			attributes.Add("previous_ms", valueOrDefault2);
		}
		return new TelemetryEvent("setup.step", Kind.Signal, Severity.Info, Gate.Required, attributes);
	}

	public static TelemetryEvent AppUncleanExit(string? priorRunId = null, string? milestone = null, long? uptimeS = null, string? breadcrumbs = null, ShutdownEvidence? shutdownEvidence = null, string? bugcheck = null, bool? activeSession = null, long? segmentIndex = null, AppExitReason? exitReason = null, long? exitSignal = null, string? trail = null)
	{
		Attributes attributes = new Attributes(11);
		if (priorRunId != null)
		{
			attributes.AddIdentifier("prior_run_id", priorRunId);
		}
		if (milestone != null)
		{
			attributes.AddIdentifier("milestone", milestone);
		}
		if (uptimeS.HasValue)
		{
			long valueOrDefault = uptimeS.GetValueOrDefault();
			attributes.Add("uptime_s", valueOrDefault);
		}
		if (breadcrumbs != null)
		{
			attributes.AddText("breadcrumbs", breadcrumbs, 8192);
		}
		if (shutdownEvidence.HasValue)
		{
			ShutdownEvidence valueOrDefault2 = shutdownEvidence.GetValueOrDefault();
			attributes.Add("shutdown_evidence", valueOrDefault2.ToWire());
		}
		if (bugcheck != null)
		{
			attributes.AddIdentifier("bugcheck", bugcheck);
		}
		if (activeSession.HasValue)
		{
			bool valueOrDefault3 = activeSession == true;
			attributes.Add("active_session", valueOrDefault3);
		}
		if (segmentIndex.HasValue)
		{
			long valueOrDefault4 = segmentIndex.GetValueOrDefault();
			attributes.Add("segment_index", valueOrDefault4);
		}
		if (exitReason.HasValue)
		{
			AppExitReason valueOrDefault5 = exitReason.GetValueOrDefault();
			attributes.Add("exit_reason", valueOrDefault5.ToWire());
		}
		if (exitSignal.HasValue)
		{
			long valueOrDefault6 = exitSignal.GetValueOrDefault();
			attributes.Add("exit_signal", valueOrDefault6);
		}
		if (trail != null)
		{
			attributes.AddText("trail", trail, 3072);
		}
		return new TelemetryEvent("app.unclean_exit", Kind.Signal, Severity.Warn, Gate.Required, attributes);
	}

	public static TelemetryEvent ClockJumped(long wallDeltaMs, long monoDeltaMs, ClockJumpSource source)
	{
		Attributes attributes = new Attributes(3);
		attributes.Add("wall_delta_ms", wallDeltaMs);
		attributes.Add("mono_delta_ms", monoDeltaMs);
		attributes.Add("source", source.ToWire());
		return new TelemetryEvent("clock.jumped", Kind.Signal, Severity.Warn, Gate.Required, attributes);
	}

	public static TelemetryEvent ConfigApplied(long configVersion, TelemetryLevel? telemetryLevel = null, string? recorderCore = null)
	{
		Attributes attributes = new Attributes(3);
		attributes.Add("config_version", configVersion);
		if (telemetryLevel.HasValue)
		{
			TelemetryLevel valueOrDefault = telemetryLevel.GetValueOrDefault();
			attributes.Add("telemetry_level", valueOrDefault.ToWire());
		}
		if (recorderCore != null)
		{
			attributes.AddIdentifier("recorder_core", recorderCore);
		}
		return new TelemetryEvent("config.applied", Kind.Signal, Severity.Info, Gate.Standard, attributes);
	}

	public static TelemetryEvent WebviewRepair(string repairKind, RepairOutcome outcome, string? runtimeVersion = null, string? errorMessage = null)
	{
		Attributes attributes = new Attributes(4);
		attributes.AddIdentifier("repair_kind", repairKind);
		attributes.Add("outcome", outcome.ToWire());
		if (runtimeVersion != null)
		{
			attributes.Add("runtime_version", runtimeVersion);
		}
		if (errorMessage != null)
		{
			attributes.AddText("error_message", errorMessage, 256);
		}
		return new TelemetryEvent("webview.repair", Kind.Signal, Severity.Warn, Gate.Standard, attributes);
	}

	public static TelemetryEvent DnsFallbackExhausted(Endpoint endpoint, string? errorMessage = null)
	{
		Attributes attributes = new Attributes(2);
		attributes.Add("endpoint", endpoint.ToWire());
		if (errorMessage != null)
		{
			attributes.AddText("error_message", errorMessage, 256);
		}
		return new TelemetryEvent("dns.fallback_exhausted", Kind.Signal, Severity.Warn, Gate.Required, attributes);
	}

	public static TelemetryEvent TelemetryBudgetExceeded(long dropped, long? windowS = null)
	{
		Attributes attributes = new Attributes(2);
		attributes.Add("dropped", dropped);
		if (windowS.HasValue)
		{
			long valueOrDefault = windowS.GetValueOrDefault();
			attributes.Add("window_s", valueOrDefault);
		}
		return new TelemetryEvent("telemetry.budget_exceeded", Kind.Signal, Severity.Warn, Gate.Required, attributes);
	}

	public static TelemetryEvent UiCommandForwardFailed(long attempts, string? command = null)
	{
		Attributes attributes = new Attributes(2);
		attributes.Add("attempts", attempts);
		if (command != null)
		{
			attributes.AddIdentifier("command", command);
		}
		return new TelemetryEvent("ui.command_forward_failed", Kind.Signal, Severity.Warn, Gate.Required, attributes);
	}

	public static TelemetryEvent DiagnosticsVerboseLine(string area, string line, string? source = null)
	{
		Attributes attributes = new Attributes(3);
		attributes.AddIdentifier("area", area);
		attributes.AddText("line", line, 2048);
		if (source != null)
		{
			attributes.AddIdentifier("source", source);
		}
		return new TelemetryEvent("diagnostics.verbose_line", Kind.Signal, Severity.Debug, Gate.Verbose, attributes);
	}

	public static TelemetryEvent TaskRunStarted(string runId, string? taskId = null, string? mode = null)
	{
		Attributes attributes = new Attributes(3);
		attributes.AddIdentifier("run_id", runId);
		if (taskId != null)
		{
			attributes.AddIdentifier("task_id", taskId);
		}
		if (mode != null)
		{
			attributes.AddIdentifier("mode", mode);
		}
		return new TelemetryEvent("task.run_started", Kind.Signal, Severity.Info, Gate.Standard, attributes);
	}

	public static TelemetryEvent TaskRunSubmitted(string runId, long? durationMs = null, long? artifacts = null)
	{
		Attributes attributes = new Attributes(3);
		attributes.AddIdentifier("run_id", runId);
		if (durationMs.HasValue)
		{
			long valueOrDefault = durationMs.GetValueOrDefault();
			attributes.Add("duration_ms", valueOrDefault);
		}
		if (artifacts.HasValue)
		{
			long valueOrDefault2 = artifacts.GetValueOrDefault();
			attributes.Add("artifacts", valueOrDefault2);
		}
		return new TelemetryEvent("task.run_submitted", Kind.Signal, Severity.Info, Gate.Standard, attributes);
	}

	public static TelemetryEvent TaskRunAbandoned(string runId, string? reason = null)
	{
		Attributes attributes = new Attributes(2);
		attributes.AddIdentifier("run_id", runId);
		if (reason != null)
		{
			attributes.AddIdentifier("reason", reason);
		}
		return new TelemetryEvent("task.run_abandoned", Kind.Signal, Severity.Warn, Gate.Standard, attributes);
	}

	public static TelemetryEvent TaskRunSuperseded(string runId, string? byRunId = null)
	{
		Attributes attributes = new Attributes(2);
		attributes.AddIdentifier("run_id", runId);
		if (byRunId != null)
		{
			attributes.AddIdentifier("by_run_id", byRunId);
		}
		return new TelemetryEvent("task.run_superseded", Kind.Signal, Severity.Warn, Gate.Standard, attributes);
	}

	public static TelemetryEvent TaskDeliveryFailed(ErrorKind errorKind, string? runId = null, string? artifactId = null, long? httpStatus = null, string? errorMessage = null)
	{
		Attributes attributes = new Attributes(5);
		attributes.Add("error_kind", errorKind.ToWire());
		if (runId != null)
		{
			attributes.AddIdentifier("run_id", runId);
		}
		if (artifactId != null)
		{
			attributes.AddIdentifier("artifact_id", artifactId);
		}
		if (httpStatus.HasValue)
		{
			long valueOrDefault = httpStatus.GetValueOrDefault();
			attributes.Add("http_status", valueOrDefault);
		}
		if (errorMessage != null)
		{
			attributes.AddText("error_message", errorMessage, 256);
		}
		return new TelemetryEvent("task.delivery_failed", Kind.Signal, Severity.Warn, Gate.Required, attributes);
	}

	public static TelemetryEvent EnrollSeatStatus(SeatStatus status)
	{
		Attributes attributes = new Attributes(1);
		attributes.Add("status", status.ToWire());
		return new TelemetryEvent("enroll.seat_status", Kind.Signal, Severity.Info, Gate.Standard, attributes);
	}

	public static TelemetryEvent EnrollFailed(SeatStatus status, string? errorMessage = null)
	{
		Attributes attributes = new Attributes(2);
		attributes.Add("status", status.ToWire());
		if (errorMessage != null)
		{
			attributes.AddText("error_message", errorMessage, 256);
		}
		return new TelemetryEvent("enroll.failed", Kind.Signal, Severity.Error, Gate.Required, attributes);
	}
}
