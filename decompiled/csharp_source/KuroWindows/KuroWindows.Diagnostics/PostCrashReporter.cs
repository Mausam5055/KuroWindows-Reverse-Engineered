using System;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClientTelemetry;
using KuroWindows.Update;

namespace KuroWindows.Diagnostics;

internal static class PostCrashReporter
{
	private sealed record PriorRun
	{
		[JsonPropertyName("run_id")]
		public string? RunId { get; init; }

		[JsonPropertyName("started_at")]
		public DateTimeOffset StartedAt { get; init; }

		[JsonPropertyName("last_seen_at")]
		public DateTimeOffset LastSeenAt { get; init; }

		[JsonPropertyName("app_version")]
		public string? AppVersion { get; init; }

		[JsonPropertyName("process_id")]
		public int ProcessId { get; init; }

		[JsonPropertyName("milestone")]
		public string? Milestone { get; init; }

		[JsonPropertyName("exit_reason")]
		public string? ExitReason { get; init; }

		[JsonPropertyName("last_exception_type")]
		public string? LastExceptionType { get; init; }

		[JsonPropertyName("last_exception_message")]
		public string? LastExceptionMessage { get; init; }

		[JsonPropertyName("last_exception_detail")]
		public string? LastExceptionDetail { get; init; }

		[JsonPropertyName("last_error_source")]
		public string? LastErrorSource { get; init; }

		[JsonPropertyName("last_error_type")]
		public string? LastErrorType { get; init; }

		[JsonPropertyName("last_error_message")]
		public string? LastErrorMessage { get; init; }

		[JsonPropertyName("clean_shutdown")]
		public bool? CleanShutdown { get; init; }
	}

	private sealed record RuntimeExceptionInfo(DateTimeOffset Time, string? Exception, string[] TopFrames);

	private sealed record CrashEvent(DateTimeOffset? Time, string? FaultingModule, string? ExceptionCode, string? FaultOffset, string? ProcessId, string? FaultingModulePath, string? ReportId, string Source, string? HangType)
	{
		public bool IsHang => Source == "hang";
	}

	private sealed record DumpEvidence(string FileName, long SizeBytes, DateTimeOffset WrittenAt, int? Pid);

	private sealed record ShutdownRecord(ShutdownEvidence Kind, int EventId, DateTimeOffset Time, string? Detail, string? Bugcheck);

	private sealed record SessionEvidence(string SessionId, int? SegmentIndex, string? ManifestState, string? VideoFile, long? VideoBytes, long? FrameCount, bool HasActiveMarker);

	private sealed record ReportedCrashState([property: JsonPropertyName("keys")] IReadOnlyList<string> Keys);

	private const int MaxReportedKeys = 64;

	private const int MaxProbeCrashReports = 8;

	private const int BreadcrumbsMaxChars = 8192;

	private const int ApplicationErrorEventId = 1000;

	private const int ApplicationHangEventId = 1002;

	private const string HangSource = "hang";

	private const string ClrExceptionCode = "0xe0434352";

	private static readonly TimeSpan RuntimeExceptionLastSeenGrace = TimeSpan.FromMinutes(1.0);

	private static readonly TimeSpan EventLookbackPadding = TimeSpan.FromMinutes(10.0);

	private static readonly TimeSpan CleanRunCrashLookback = TimeSpan.FromDays(1.0);

	private static readonly TimeSpan MaxCrashEventLookback = TimeSpan.FromDays(7.0);

	private static readonly string DiagnosticsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppConfig.LocalDataFolderName, "diagnostics");

	private static readonly string RunStatePath = Path.Combine(DiagnosticsDir, "last-run.json");

	private static readonly string ReportedPath = Path.Combine(DiagnosticsDir, "reported-crashes.json");

	private static readonly string DataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppConfig.LocalDataFolderName, ".data");

	private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
	{
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
		WriteIndented = true
	};

	public static void ReportPreviousCrashIfAny()
	{
		try
		{
			PriorRun priorRun = ReadPriorRun();
			DateTimeOffset utcNow = DateTimeOffset.UtcNow;
			bool flag = (((object)priorRun != null && !(priorRun.CleanShutdown ?? false)) ? true : false);
			PriorRun priorRun2 = (flag ? priorRun : null);
			AppUpdates.ReportPreviousRun(priorRun?.AppVersion, priorRun?.ExitReason);
			DateTimeOffset dateTimeOffset = EventSearchStart(priorRun, utcNow);
			IReadOnlyList<UiaProbeRegistry.Entry> probeRuns = UiaProbeRegistry.Read();
			List<CrashEvent> helperCrashes = new List<CrashEvent>();
			CrashEvent crashEvent = FindLatestApplicationCrash(dateTimeOffset, probeRuns, priorRun?.ProcessId, helperCrashes);
			DumpEvidence dumpEvidence = FindLatestDump(dateTimeOffset, probeRuns, priorRun?.ProcessId);
			if (priorRun2 != null && priorRun2.StartedAt != default(DateTimeOffset))
			{
				DateTimeOffset? dateTimeOffset2 = crashEvent?.Time;
				if (dateTimeOffset2.HasValue)
				{
					DateTimeOffset valueOrDefault = dateTimeOffset2.GetValueOrDefault();
					if (valueOrDefault < priorRun2.StartedAt || valueOrDefault > utcNow)
					{
						crashEvent = null;
					}
				}
				if (dumpEvidence != null && (dumpEvidence.WrittenAt < priorRun2.StartedAt || dumpEvidence.WrittenAt > utcNow))
				{
					dumpEvidence = null;
				}
			}
			DateTimeOffset dateTimeOffset3 = dateTimeOffset;
			if (crashEvent == null && priorRun2 != null && priorRun2.StartedAt != default(DateTimeOffset) && priorRun2.StartedAt > dateTimeOffset3)
			{
				dateTimeOffset3 = priorRun2.StartedAt;
			}
			RuntimeExceptionInfo runtimeExceptionInfo = FindLatestRuntimeExceptionInfo(dateTimeOffset3, crashEvent?.Time, probeRuns, priorRun?.ProcessId, priorRun2?.LastSeenAt);
			UiaBreadcrumb.InFlightRecord breadcrumb = UiaBreadcrumb.TryReadInFlight();
			UiaBreadcrumb.Clear();
			ReportProbeHelperCrashes(helperCrashes);
			bool flag2 = crashEvent != null || dumpEvidence != null || runtimeExceptionInfo != null;
			if (priorRun2 == null && !flag2)
			{
				return;
			}
			SessionEvidence sessionEvidence = FindLatestSessionEvidence();
			ShutdownRecord shutdownRecord = ((priorRun2 != null) ? FindShutdownEvidence(dateTimeOffset, priorRun2.LastSeenAt) : null);
			List<string> list = new List<string>();
			IReadOnlyList<int> readOnlyList = UiaProbeRegistry.EventPidReadings(crashEvent?.ProcessId);
			int num;
			if ((object)priorRun != null && priorRun.ProcessId > 0)
			{
				if (readOnlyList.Count <= 0 || readOnlyList.Contains(priorRun.ProcessId))
				{
					if ((object)dumpEvidence != null)
					{
						int? pid = dumpEvidence.Pid;
						if (pid.HasValue && pid.GetValueOrDefault() > 0)
						{
							num = ((dumpEvidence.Pid != priorRun.ProcessId) ? 1 : 0);
							goto IL_02e8;
						}
					}
					num = 0;
				}
				else
				{
					num = 1;
				}
			}
			else
			{
				num = 0;
			}
			goto IL_02e8;
			IL_063f:
			bool flag3;
			if (flag2 && !flag3)
			{
				string text = BuildDedupeKey(null, crashEvent, dumpEvidence, runtimeExceptionInfo, null);
				if (!WasAlreadyReported(text))
				{
					list.Add(text);
					string text2 = "previous_process_crash_detected " + DescribePriorRun(priorRun) + DescribeCrash(crashEvent) + DescribeRuntimeException(runtimeExceptionInfo) + DescribeDump(dumpEvidence) + DescribeShutdownEvidence(null, hasUncleanRun: false) + DescribeBreadcrumb(null, hasUncleanRun: false) + DescribeSession(sessionEvidence);
					KuroLog.Crash(text2, "ReportPreviousCrashIfAny", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Diagnostics\\PostCrashReporter.cs", 245);
					CrashKind crashKind = KindOf(crashEvent, runtimeExceptionInfo, managedFatal: false);
					string? exceptionType = ExceptionTypeName(runtimeExceptionInfo?.Exception);
					string? signalName = crashEvent?.ExceptionCode;
					string? faultModule = crashEvent?.FaultingModule;
					string breadcrumbs = text2;
					Telemetry.Emit(Ev.AppCrashed(crashKind, exceptionType, signalName, faultModule, null, null, null, breadcrumbs));
				}
			}
			Telemetry.FlushToDisk();
			foreach (string item in list)
			{
				RememberReported(item);
			}
			return;
			IL_032d:
			int num2;
			bool flag4 = (byte)num2 != 0;
			CrashEvent crashEvent2 = (flag3 ? crashEvent : null);
			DumpEvidence dumpEvidence2 = (flag3 ? dumpEvidence : null);
			RuntimeExceptionInfo runtimeExceptionInfo2 = (flag3 ? runtimeExceptionInfo : null);
			string text3 = BuildDedupeKey(priorRun2.RunId, crashEvent2, dumpEvidence2, runtimeExceptionInfo2, sessionEvidence);
			if (!WasAlreadyReported(text3))
			{
				list.Add(text3);
				Count.SessionDirtyStart(1L);
				long? uptimeS = ((priorRun2.StartedAt != default(DateTimeOffset) && priorRun2.LastSeenAt != default(DateTimeOffset)) ? new long?(Math.Max(0L, (long)(priorRun2.LastSeenAt - priorRun2.StartedAt).TotalSeconds)) : ((long?)null));
				ShutdownEvidence value = shutdownRecord?.Kind ?? ShutdownEvidence.None;
				bool flag5 = flag4 || crashEvent2 != null || dumpEvidence2 != null || runtimeExceptionInfo2 != null;
				string text4 = (flag5 ? "previous_run_crash_detected " : "previous_run_unclean_no_crash_event ") + DescribePriorRun(priorRun2) + (flag5 ? (DescribeCrash(crashEvent2) + DescribeRuntimeException(runtimeExceptionInfo2) + DescribeDump(dumpEvidence2)) : "") + DescribeShutdownEvidence(shutdownRecord, hasUncleanRun: true) + DescribeBreadcrumb(breadcrumb, hasUncleanRun: true) + DescribeSession(sessionEvidence);
				KuroLog.Crash(text4, "ReportPreviousCrashIfAny", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Diagnostics\\PostCrashReporter.cs", 190);
				if (flag5)
				{
					Telemetry.Emit(Ev.AppCrashed(KindOf(crashEvent2, runtimeExceptionInfo2, flag4), ExceptionTypeName(runtimeExceptionInfo2?.Exception ?? (flag4 ? priorRun2.LastExceptionType : null)), crashEvent2?.ExceptionCode, crashEvent2?.FaultingModule, priorRun2.RunId, priorRun2.Milestone, uptimeS, Breadcrumbs(text4, priorRun2), value, shutdownRecord?.Bugcheck, sessionEvidence?.HasActiveMarker, sessionEvidence?.SegmentIndex));
				}
				else
				{
					Telemetry.Emit(Ev.AppUncleanExit(priorRun2.RunId, priorRun2.Milestone, uptimeS, Breadcrumbs(text4, priorRun2), value, shutdownRecord?.Bugcheck, sessionEvidence?.HasActiveMarker, sessionEvidence?.SegmentIndex));
				}
			}
			goto IL_063f;
			IL_02e8:
			bool flag6 = (byte)num != 0;
			flag3 = priorRun2 != null && !flag6;
			if (priorRun2 != null)
			{
				if ((object)priorRun2 != null && priorRun2.ExitReason != null)
				{
					string breadcrumbs = priorRun2.LastExceptionType;
					if (breadcrumbs != null)
					{
						num2 = ((breadcrumbs.Length > 0) ? 1 : 0);
						goto IL_032d;
					}
				}
				num2 = 0;
				goto IL_032d;
			}
			goto IL_063f;
		}
		catch (Exception ex)
		{
			KuroLog.Crash("post-crash reporter failed: " + ex.Message, "ReportPreviousCrashIfAny", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Diagnostics\\PostCrashReporter.cs", 265);
			Telemetry.FlushToDisk();
		}
	}

	private static CrashKind KindOf(CrashEvent? crash, RuntimeExceptionInfo? runtime, bool managedFatal)
	{
		if ((object)crash == null || !crash.IsHang)
		{
			if (!managedFatal && !(runtime != null) && !(crash?.ExceptionCode == "0xe0434352"))
			{
				if (!(crash != null))
				{
					return CrashKind.WerDump;
				}
				return CrashKind.NativeSignal;
			}
			return CrashKind.UnhandledException;
		}
		return CrashKind.Hang;
	}

	private static string Breadcrumbs(string description, PriorRun prior)
	{
		StringBuilder stringBuilder = new StringBuilder(description);
		string lastExceptionDetail = prior.LastExceptionDetail;
		if (lastExceptionDetail != null && lastExceptionDetail.Length > 0)
		{
			stringBuilder.Append("\nstack=").Append(lastExceptionDetail);
		}
		string text = KuroLog.ReadTail(prior.StartedAt, KuroLog.ProcessStartedAt, 8192 - stringBuilder.Length - "\ntail:\n".Length);
		if (text.Length > 0)
		{
			stringBuilder.Append("\ntail:\n").Append(text);
		}
		return stringBuilder.ToString();
	}

	private static string? ExceptionTypeName(string? exception)
	{
		if (string.IsNullOrWhiteSpace(exception))
		{
			return null;
		}
		string text = exception.Split(':', 2)[0].Trim();
		int num = text.LastIndexOf('.');
		string text2 = ((num >= 0) ? text.Substring(num + 1) : text);
		if (text2.Length > 128)
		{
			return text2.Substring(0, 128);
		}
		return text2;
	}

	private static DateTimeOffset EventSearchStart(PriorRun? prior, DateTimeOffset now)
	{
		bool flag = (((object)prior != null && !(prior.CleanShutdown ?? false)) ? true : false);
		DateTimeOffset dateTimeOffset;
		if (flag)
		{
			dateTimeOffset = ((prior.StartedAt == default(DateTimeOffset)) ? (now - MaxCrashEventLookback) : (prior.StartedAt - EventLookbackPadding));
		}
		else if (prior != null && prior.StartedAt != default(DateTimeOffset))
		{
			dateTimeOffset = prior.StartedAt;
		}
		else
		{
			dateTimeOffset = ((!(prior != null) || !(prior.LastSeenAt != default(DateTimeOffset))) ? (now - CleanRunCrashLookback) : (prior.LastSeenAt - EventLookbackPadding));
		}
		if (!(dateTimeOffset < now - MaxCrashEventLookback))
		{
			return dateTimeOffset;
		}
		return now - MaxCrashEventLookback;
	}

	private static PriorRun? ReadPriorRun()
	{
		try
		{
			if (!File.Exists(RunStatePath))
			{
				return null;
			}
			using FileStream utf8Json = File.OpenRead(RunStatePath);
			return JsonSerializer.Deserialize<PriorRun>(utf8Json, JsonOptions);
		}
		catch (Exception ex)
		{
			KuroLog.LocalOnly("crash", "prior-run state read failed: " + ex.Message, "ReadPriorRun", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Diagnostics\\PostCrashReporter.cs", 354);
			return null;
		}
	}

	private static CrashEvent? FindLatestApplicationCrash(DateTimeOffset start, IReadOnlyList<UiaProbeRegistry.Entry> probeRuns, int? priorRunPid, List<CrashEvent> helperCrashes)
	{
		try
		{
			using EventLogReader eventLogReader = new EventLogReader(new EventLogQuery("Application", PathType.LogName, $"*[System[(EventID={1000} or EventID={1002})]]")
			{
				ReverseDirection = true
			});
			for (int i = 0; i < 80; i++)
			{
				using EventRecord eventRecord = eventLogReader.ReadEvent();
				if (eventRecord == null)
				{
					return null;
				}
				DateTime? timeCreated = eventRecord.TimeCreated;
				if (!timeCreated.HasValue || new DateTimeOffset(timeCreated.GetValueOrDefault().ToUniversalTime(), TimeSpan.Zero) < start)
				{
					return null;
				}
				CrashEvent crashEvent = TryReadApplicationError(eventRecord);
				if (crashEvent != null)
				{
					if (!UiaProbeRegistry.IsHelperEventRecord(probeRuns, crashEvent.ProcessId, crashEvent.Time, priorRunPid))
					{
						return crashEvent;
					}
					helperCrashes.Add(crashEvent);
				}
			}
		}
		catch (Exception ex)
		{
			KuroLog.Crash("post-crash event-log scan failed: " + ex.Message, "FindLatestApplicationCrash", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Diagnostics\\PostCrashReporter.cs", 409);
		}
		return null;
	}

	private static CrashEvent? TryReadApplicationError(EventRecord record)
	{
		try
		{
			string[] array = record.Properties.Select((EventProperty p) => p.Value?.ToString() ?? "").ToArray();
			if (array.Length == 0 || !string.Equals(array[0], "KuroWindows.exe", StringComparison.OrdinalIgnoreCase))
			{
				return null;
			}
			DateTimeOffset? time = (record.TimeCreated.HasValue ? new DateTimeOffset?(new DateTimeOffset(record.TimeCreated.Value.ToUniversalTime(), TimeSpan.Zero)) : ((DateTimeOffset?)null));
			if (record.Id == 1002)
			{
				return new CrashEvent(time, null, "hang", null, At(array, 2), At(array, 5), At(array, 6), "hang", At(array, 9));
			}
			if (array.Length < 12)
			{
				return null;
			}
			return new CrashEvent(time, array[3], NormalizeHex(array[6]), NormalizeHex(array[7]), array[8], array[11], At(array, 12), "event_log", null);
		}
		catch
		{
			return null;
		}
		static string? At(string[] values, int index)
		{
			if (index >= values.Length || values[index].Length <= 0)
			{
				return null;
			}
			return values[index];
		}
	}

	private static RuntimeExceptionInfo? FindLatestRuntimeExceptionInfo(DateTimeOffset start, DateTimeOffset? crashTime, IReadOnlyList<UiaProbeRegistry.Entry> probeRuns, int? priorRunPid, DateTimeOffset? uncleanRunLastSeen)
	{
		try
		{
			using EventLogReader eventLogReader = new EventLogReader(new EventLogQuery("Application", PathType.LogName, "*[System[Provider[@Name='.NET Runtime'] and (EventID=1026)]]")
			{
				ReverseDirection = true
			});
			for (int i = 0; i < 80; i++)
			{
				using EventRecord eventRecord = eventLogReader.ReadEvent();
				if (eventRecord == null)
				{
					return null;
				}
				DateTime? timeCreated = eventRecord.TimeCreated;
				if (!timeCreated.HasValue)
				{
					continue;
				}
				DateTimeOffset dateTimeOffset = new DateTimeOffset(timeCreated.GetValueOrDefault().ToUniversalTime(), TimeSpan.Zero);
				if (dateTimeOffset < start)
				{
					return null;
				}
				if ((!crashTime.HasValue || !((crashTime.Value - dateTimeOffset).Duration() > TimeSpan.FromMinutes(5.0))) && !UiaProbeRegistry.IsHelperRecord(probeRuns, TryReadRecordPid(eventRecord), dateTimeOffset, priorRunPid))
				{
					if (!uncleanRunLastSeen.HasValue)
					{
						goto IL_0106;
					}
					DateTimeOffset valueOrDefault = uncleanRunLastSeen.GetValueOrDefault();
					if (!(valueOrDefault != default(DateTimeOffset)) || !(dateTimeOffset < valueOrDefault - RuntimeExceptionLastSeenGrace))
					{
						goto IL_0106;
					}
				}
				goto end_IL_002b;
				IL_0106:
				RuntimeExceptionInfo runtimeExceptionInfo = TryParseRuntimeException(eventRecord, dateTimeOffset);
				if (runtimeExceptionInfo != null)
				{
					return runtimeExceptionInfo;
				}
				end_IL_002b:;
			}
		}
		catch (Exception ex)
		{
			KuroLog.LocalOnly("crash", "runtime-exception event scan failed: " + ex.Message, "FindLatestRuntimeExceptionInfo", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Diagnostics\\PostCrashReporter.cs", 550);
		}
		return null;
	}

	private static RuntimeExceptionInfo? TryParseRuntimeException(EventRecord record, DateTimeOffset time)
	{
		try
		{
			string text = ((record.Properties.Count <= 0) ? null : record.Properties[0].Value?.ToString());
			if (string.IsNullOrWhiteSpace(text) || !text.Contains("Application: KuroWindows.exe", StringComparison.OrdinalIgnoreCase))
			{
				return null;
			}
			string text2 = null;
			List<string> list = new List<string>();
			string[] array = text.Split('\n');
			for (int i = 0; i < array.Length; i++)
			{
				string text3 = array[i].Trim();
				if (text2 == null && text3.StartsWith("Exception Info: ", StringComparison.OrdinalIgnoreCase))
				{
					text2 = text3.Substring("Exception Info: ".Length);
				}
				else if (text3.StartsWith("at ", StringComparison.Ordinal) && list.Count < 3)
				{
					list.Add(text3.Substring(3));
				}
			}
			return (text2 == null && list.Count == 0) ? null : new RuntimeExceptionInfo(time, text2, list.ToArray());
		}
		catch
		{
			return null;
		}
	}

	private static void ReportProbeHelperCrashes(List<CrashEvent> helperCrashes)
	{
		int num = 0;
		foreach (CrashEvent helperCrash in helperCrashes)
		{
			if (num >= 8)
			{
				break;
			}
			string key = "uia_probe|" + string.Join("|", helperCrash.Time?.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture) ?? "-", helperCrash.ProcessId ?? "-", helperCrash.FaultingModule ?? "-", helperCrash.ExceptionCode ?? "-", helperCrash.FaultOffset ?? "-");
			if (!WasAlreadyReported(key))
			{
				KuroLog.Crash("uia_probe_crash_detected probe_crashes_found=" + helperCrashes.Count.ToString(CultureInfo.InvariantCulture) + " probe_crashes_reported_cap=" + 8.ToString(CultureInfo.InvariantCulture) + " " + DescribeCrash(helperCrash), "ReportProbeHelperCrashes", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Diagnostics\\PostCrashReporter.cs", 630);
				Count.UiaProbe(UiaProbeEvent.CrashDetected, 1L);
				RememberReported(key);
				num++;
			}
		}
	}

	private static int? TryReadRecordPid(EventRecord record)
	{
		try
		{
			return record.ProcessId;
		}
		catch
		{
			return null;
		}
	}

	private static ShutdownRecord? FindShutdownEvidence(DateTimeOffset start, DateTimeOffset? lastSeen)
	{
		try
		{
			using EventLogReader eventLogReader = new EventLogReader(new EventLogQuery("System", PathType.LogName, "*[System[(EventID=1074 or EventID=6008 or EventID=41)]]")
			{
				ReverseDirection = true
			});
			ShutdownRecord shutdownRecord = null;
			for (int i = 0; i < 80; i++)
			{
				using EventRecord eventRecord = eventLogReader.ReadEvent();
				if (eventRecord == null)
				{
					break;
				}
				DateTime? timeCreated = eventRecord.TimeCreated;
				if (!timeCreated.HasValue)
				{
					continue;
				}
				DateTimeOffset dateTimeOffset = new DateTimeOffset(timeCreated.GetValueOrDefault().ToUniversalTime(), TimeSpan.Zero);
				if (dateTimeOffset < start)
				{
					break;
				}
				ShutdownRecord shutdownRecord2 = ClassifyShutdownRecord(eventRecord, dateTimeOffset, lastSeen);
				if (!(shutdownRecord2 == null))
				{
					if (shutdownRecord2.Kind == ShutdownEvidence.OsShutdown)
					{
						return shutdownRecord2;
					}
					if ((object)shutdownRecord == null)
					{
						shutdownRecord = shutdownRecord2;
					}
				}
				continue;
			}
			return shutdownRecord;
		}
		catch (Exception ex)
		{
			KuroLog.LocalOnly("crash", "shutdown-evidence event scan failed: " + ex.Message, "FindShutdownEvidence", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Diagnostics\\PostCrashReporter.cs", 719);
			return null;
		}
	}

	private static ShutdownRecord? ClassifyShutdownRecord(EventRecord record, DateTimeOffset recordTime, DateTimeOffset? lastSeen)
	{
		int num;
		if (lastSeen.HasValue)
		{
			DateTimeOffset valueOrDefault = lastSeen.GetValueOrDefault();
			num = ((valueOrDefault != default(DateTimeOffset)) ? 1 : 0);
		}
		else
		{
			num = 0;
		}
		bool flag = (byte)num != 0;
		switch (record.Id)
		{
		case 1074:
		{
			if (flag && (recordTime < lastSeen.Value - TimeSpan.FromMinutes(2.0) || recordTime > lastSeen.Value + TimeSpan.FromMinutes(15.0)))
			{
				return null;
			}
			string text2 = Property(record, 0);
			string text3 = Property(record, 4);
			return new ShutdownRecord(ShutdownEvidence.OsShutdown, record.Id, recordTime, "initiator=" + (text2 ?? "-") + " type=" + (text3 ?? "-"), null);
		}
		case 6008:
			if (flag && recordTime < lastSeen.Value)
			{
				return null;
			}
			return new ShutdownRecord(ShutdownEvidence.UnexpectedPower, record.Id, recordTime, null, null);
		case 41:
		{
			if (flag && recordTime < lastSeen.Value)
			{
				return null;
			}
			string text = ((ulong.TryParse(Property(record, 0), NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) && result != 0L) ? $"0x{result:x}" : null);
			return new ShutdownRecord(ShutdownEvidence.UnexpectedPower, record.Id, recordTime, (text == null) ? null : ("bugcheck=" + text), text);
		}
		default:
			return null;
		}
		static string? Property(EventRecord eventRecord, int index)
		{
			try
			{
				return (eventRecord.Properties.Count <= index) ? null : eventRecord.Properties[index].Value?.ToString();
			}
			catch
			{
				return null;
			}
		}
	}

	private static DumpEvidence? FindLatestDump(DateTimeOffset start, IReadOnlyList<UiaProbeRegistry.Entry> probeRuns, int? priorRunPid)
	{
		DumpEvidence dumpEvidence = null;
		foreach (string item in DumpRoots())
		{
			try
			{
				if (!Directory.Exists(item))
				{
					continue;
				}
				foreach (string item2 in Directory.EnumerateFiles(item, "KuroWindows*.dmp"))
				{
					FileInfo fileInfo = new FileInfo(item2);
					if (fileInfo.LastWriteTimeUtc < start.UtcDateTime)
					{
						continue;
					}
					DumpEvidence dumpEvidence2 = new DumpEvidence(fileInfo.Name, fileInfo.Length, new DateTimeOffset(fileInfo.LastWriteTimeUtc, TimeSpan.Zero), UiaProbeRegistry.ParseDumpPid(fileInfo.Name));
					if (UiaProbeRegistry.IsHelperRecord(probeRuns, dumpEvidence2.Pid, dumpEvidence2.WrittenAt, priorRunPid))
					{
						try
						{
							fileInfo.Delete();
						}
						catch
						{
						}
					}
					else if (dumpEvidence == null || dumpEvidence2.WrittenAt > dumpEvidence.WrittenAt)
					{
						dumpEvidence = dumpEvidence2;
					}
				}
			}
			catch
			{
			}
		}
		return dumpEvidence;
	}

	private static SessionEvidence? FindLatestSessionEvidence()
	{
		try
		{
			if (!Directory.Exists(DataRoot))
			{
				return null;
			}
			DirectoryInfo directoryInfo = (from path2 in Directory.EnumerateDirectories(DataRoot)
				select new DirectoryInfo(path2) into info
				orderby info.LastWriteTimeUtc descending
				select info).FirstOrDefault();
			if (directoryInfo == null)
			{
				return null;
			}
			string text = Directory.EnumerateFiles(directoryInfo.FullName, "segment_*.manifest.json").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
			bool hasActiveMarker = File.Exists(Path.Combine(directoryInfo.FullName, ".active-session.json"));
			if (text == null)
			{
				return new SessionEvidence(directoryInfo.Name, null, null, null, null, null, hasActiveMarker);
			}
			using FileStream utf8Json = File.OpenRead(text);
			using JsonDocument jsonDocument = JsonDocument.Parse(utf8Json);
			JsonElement rootElement = jsonDocument.RootElement;
			int? segmentIndex = TryGetInt(rootElement, "segment_index");
			string manifestState = TryGetString(rootElement, "state");
			string text2 = null;
			long? videoBytes = null;
			long? num = null;
			long? num2 = null;
			if (rootElement.TryGetProperty("displays", out var value) && value.ValueKind == JsonValueKind.Array && value.GetArrayLength() > 0)
			{
				JsonElement element = value[0];
				text2 = TryGetString(element, "video_file");
				num2 = TryGetLong(element, "encoded_frame_count");
				if (!string.IsNullOrWhiteSpace(text2))
				{
					string text3 = Path.Combine(directoryInfo.FullName, text2);
					if (File.Exists(text3))
					{
						videoBytes = new FileInfo(text3).Length;
					}
					string path = Path.ChangeExtension(text3, ".frames");
					if (File.Exists(path) && long.TryParse(File.ReadAllText(path).Trim(), out var result))
					{
						num = result;
					}
				}
			}
			return new SessionEvidence(directoryInfo.Name, segmentIndex, manifestState, text2, videoBytes, num ?? num2, hasActiveMarker);
		}
		catch
		{
			return null;
		}
	}

	private static bool WasAlreadyReported(string key)
	{
		try
		{
			return ReadReportedState().Keys.Contains(key, StringComparer.Ordinal);
		}
		catch
		{
			return false;
		}
	}

	private static void RememberReported(string key)
	{
		try
		{
			string[] keys = ReadReportedState().Keys.Where((string existing) => !string.Equals(existing, key, StringComparison.Ordinal)).Append(key).TakeLast(64)
				.ToArray();
			Directory.CreateDirectory(DiagnosticsDir);
			File.WriteAllText(ReportedPath, JsonSerializer.Serialize(new ReportedCrashState(keys), JsonOptions));
		}
		catch (Exception ex)
		{
			KuroLog.LocalOnly("crash", "reported-crash dedupe state write failed: " + ex.Message, "RememberReported", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Diagnostics\\PostCrashReporter.cs", 961);
		}
	}

	private static ReportedCrashState ReadReportedState()
	{
		try
		{
			if (!File.Exists(ReportedPath))
			{
				return new ReportedCrashState(Array.Empty<string>());
			}
			using FileStream utf8Json = File.OpenRead(ReportedPath);
			return JsonSerializer.Deserialize<ReportedCrashState>(utf8Json, JsonOptions) ?? new ReportedCrashState(Array.Empty<string>());
		}
		catch
		{
			return new ReportedCrashState(Array.Empty<string>());
		}
	}

	private static string BuildDedupeKey(string? runId, CrashEvent? crash, DumpEvidence? dump, RuntimeExceptionInfo? runtime, SessionEvidence? session)
	{
		string[] value = new string[10]
		{
			runId ?? "-",
			crash?.Time?.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture) ?? "-",
			crash?.FaultingModule ?? "-",
			crash?.ExceptionCode ?? "-",
			crash?.FaultOffset ?? "-",
			crash?.ProcessId ?? "-",
			dump?.FileName ?? "-",
			dump?.WrittenAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture) ?? "-",
			runtime?.Time.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture) ?? "-",
			session?.SessionId ?? "-"
		};
		return string.Join("|", value);
	}

	private static string DescribePriorRun(PriorRun? prior)
	{
		if (prior == null)
		{
			return "run_id=- started_at=- last_seen_at=- process_id=- milestone=- exit_reason=- clean_shutdown=- app_version=- ";
		}
		return $"run_id={Safe(prior.RunId)} started_at={FormatTime(prior.StartedAt)} last_seen_at={FormatTime(prior.LastSeenAt)} process_id={((prior.ProcessId > 0) ? prior.ProcessId.ToString(CultureInfo.InvariantCulture) : "-")} milestone={Safe(prior.Milestone)} exit_reason={Safe(prior.ExitReason)} last_exception={SafeExceptionPair(prior.LastExceptionType, prior.LastExceptionMessage)} last_error={SafeExceptionPair(prior.LastErrorType, prior.LastErrorMessage)} clean_shutdown={prior.CleanShutdown?.ToString() ?? "-"} app_version={Safe(prior.AppVersion)} ";
	}

	private static string SafeExceptionPair(string? type, string? message)
	{
		if (string.IsNullOrWhiteSpace(type))
		{
			return "-";
		}
		if (!string.IsNullOrWhiteSpace(message))
		{
			return Safe(type + ": " + message);
		}
		return Safe(type);
	}

	private static string DescribeCrash(CrashEvent? crash)
	{
		if (crash == null)
		{
			return "fault_module=- exception_code=- fault_offset=- fault_process_id=- crash_event_time=- crash_source=- hang_type=- ";
		}
		return $"fault_module={Safe(crash.FaultingModule)} exception_code={Safe(crash.ExceptionCode)} fault_offset={Safe(crash.FaultOffset)} fault_process_id={Safe(crash.ProcessId)} fault_module_path={SafeFileName(crash.FaultingModulePath)} report_id={Safe(crash.ReportId)} crash_event_time={FormatTime(crash.Time)} crash_source={Safe(crash.Source)} hang_type={Safe(crash.HangType)} ";
	}

	private static string DescribeRuntimeException(RuntimeExceptionInfo? runtime)
	{
		if (runtime == null)
		{
			return "runtime_exception=- runtime_stack_top=- ";
		}
		string value = ((runtime.TopFrames.Length != 0) ? string.Join(" <- ", runtime.TopFrames) : null);
		return $"runtime_exception={SafeLong(runtime.Exception)} runtime_stack_top={SafeLong(value)} ";
	}

	private static string DescribeShutdownEvidence(ShutdownRecord? shutdown, bool hasUncleanRun)
	{
		if (!hasUncleanRun)
		{
			return "shutdown_evidence=- shutdown_event_id=- shutdown_event_time=- shutdown_detail=- ";
		}
		if (shutdown == null)
		{
			return "shutdown_evidence=none shutdown_event_id=- shutdown_event_time=- shutdown_detail=- ";
		}
		return $"shutdown_evidence={shutdown.Kind.ToWire()} shutdown_event_id={shutdown.EventId.ToString(CultureInfo.InvariantCulture)} shutdown_event_time={FormatTime(shutdown.Time)} shutdown_detail={Safe(shutdown.Detail)} ";
	}

	private static string DescribeBreadcrumb(UiaBreadcrumb.InFlightRecord? breadcrumb, bool hasUncleanRun)
	{
		if (!hasUncleanRun || breadcrumb == null)
		{
			return "uia_inflight_op=- uia_target_exe=- uia_target_class=- uia_target_pid=- uia_inflight_started_at=- ";
		}
		return $"uia_inflight_op={Safe(breadcrumb.Op)} uia_target_exe={Safe(breadcrumb.TargetExe)} uia_target_class={Safe(breadcrumb.TargetClass)} uia_target_pid={((breadcrumb.TargetPid > 0) ? breadcrumb.TargetPid.ToString(CultureInfo.InvariantCulture) : "-")} uia_inflight_started_at={FormatTime(breadcrumb.StartedAt)} ";
	}

	private static string DescribeDump(DumpEvidence? dump)
	{
		if (dump == null)
		{
			return "dump_file=- dump_size_mb=- dump_written_at=- ";
		}
		return $"dump_file={Safe(dump.FileName)} dump_size_mb={(double)dump.SizeBytes / 1024.0 / 1024.0:F1} dump_written_at={FormatTime(dump.WrittenAt)} ";
	}

	private static string DescribeSession(SessionEvidence? session)
	{
		if (session == null)
		{
			return "session=- segment=- manifest_state=- video_file=- video_bytes=- frame_count=- active_session=-";
		}
		return $"session={Safe(session.SessionId)} segment={session.SegmentIndex?.ToString(CultureInfo.InvariantCulture) ?? "-"} manifest_state={Safe(session.ManifestState)} video_file={Safe(session.VideoFile)} video_bytes={session.VideoBytes?.ToString(CultureInfo.InvariantCulture) ?? "-"} frame_count={session.FrameCount?.ToString(CultureInfo.InvariantCulture) ?? "-"} active_session={session.HasActiveMarker}";
	}

	private static IEnumerable<string> DumpRoots()
	{
		yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CrashDumps");
		yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppConfig.LocalDataFolderName, "dumps");
		yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppConfig.LocalDataFolderName, "dumps");
	}

	private static string? NormalizeHex(string? value)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			return null;
		}
		string text = value.Trim();
		if (!text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
		{
			return "0x" + text.ToLowerInvariant();
		}
		return text.ToLowerInvariant();
	}

	private static string Safe(string? value)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			return "-";
		}
		string text = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
		if (text.Length > 160)
		{
			return text.Substring(0, 160) + "...";
		}
		return text;
	}

	private static string SafeLong(string? value)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			return "-";
		}
		string text = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
		if (text.Length > 400)
		{
			return text.Substring(0, 400) + "...";
		}
		return text;
	}

	private static string SafeFileName(string? path)
	{
		if (string.IsNullOrWhiteSpace(path))
		{
			return "-";
		}
		try
		{
			return Path.GetFileName(path);
		}
		catch
		{
			return Safe(path);
		}
	}

	private static string FormatTime(DateTimeOffset? value)
	{
		if (!value.HasValue || !(value.Value != default(DateTimeOffset)))
		{
			return "-";
		}
		return value.Value.ToUniversalTime().ToString("O");
	}

	private static string? TryGetString(JsonElement element, string name)
	{
		if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
		{
			return null;
		}
		return value.GetString();
	}

	private static int? TryGetInt(JsonElement element, string name)
	{
		if (!element.TryGetProperty(name, out var value) || !value.TryGetInt32(out var value2))
		{
			return null;
		}
		return value2;
	}

	private static long? TryGetLong(JsonElement element, string name)
	{
		if (!element.TryGetProperty(name, out var value) || !value.TryGetInt64(out var value2))
		{
			return null;
		}
		return value2;
	}
}
