using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Windows.Forms;
using KuroWindows.RecorderCore;
using KuroWindows.RecorderCore.Capture;

namespace KuroWindows.Capture;

internal static class CaptureHealthRegistry
{
	private readonly record struct StrikeState(int Count, DateTimeOffset LastUtc);

	private readonly record struct RemoteDxgiState(int Count, int Epoch, DateTimeOffset LastUtc);

	private sealed class DisplayHealth
	{
		public volatile bool EverProducedFrame;

		public volatile bool FastPath;

		public long LastFrameUtcTicks;

		public string LastFrameSource = "";

		public int ChronicRun;

		public int RemoteRun;

		public long LastExhaustionUtcTicks;

		public readonly List<DateTimeOffset> StructuralEvidence = new List<DateTimeOffset>();
	}

	public const string SourceDxgi = "dxgi";

	public const string SourceWgcBorderless = "wgc_borderless";

	public const string SourceWgcBordered = "wgc_bordered";

	public const string SourceDdaGpu = "dda_gpu";

	public const string SourceWgcGpu = "wgc_gpu";

	public const string SourceGpuV2Core = "gpu_v2";

	public const int DemotionThreshold = 3;

	public static readonly TimeSpan DecayWindow = TimeSpan.FromMinutes(15.0);

	private const int ChronicExhaustionThreshold = 5;

	private const int RemoteExhaustionThreshold = 2;

	private const int StructuralExhaustionThreshold = 3;

	private const int StructuralBucketSpan = 2;

	private const int StructuralEvidenceCap = 128;

	private static readonly ConcurrentDictionary<string, StrikeState> Gen2TierStrikes = new ConcurrentDictionary<string, StrikeState>();

	private static readonly ConcurrentDictionary<string, StrikeState> DePreferences = new ConcurrentDictionary<string, StrikeState>();

	private static readonly ConcurrentDictionary<string, StrikeState> FailureCounts = new ConcurrentDictionary<string, StrikeState>();

	private static readonly ConcurrentDictionary<string, RemoteDxgiState> RemoteDxgiHardInits = new ConcurrentDictionary<string, RemoteDxgiState>();

	private static readonly ConcurrentDictionary<string, DisplayHealth> Displays = new ConcurrentDictionary<string, DisplayHealth>();

	private static int _lastPacing;

	internal static Func<int> EnvironmentEpochSource = () => RemoteSessionProbe.EnvironmentEpoch;

	internal static Func<bool> RemoteAttachedSource = () => RemoteSessionProbe.Kind == SessionEnvironmentKind.RemoteAttached;

	internal static Func<bool> WgcSupportedSource = () => WgcMonitorCaptureBackend.IsSupported();

	internal static Func<string?> BorderSuppressionBlockerSource = () => WgcMonitorCaptureBackend.BorderSuppressionBlocker;

	public static string KeyFor(Screen screen)
	{
		return CaptureDisplayKey.For(screen);
	}

	internal static bool IsNeverRecorded(Exception ex)
	{
		if (!CaptureAccessDeniedClassifier.IsAccessDenied(ex))
		{
			if (ex is CaptureEncoderStallException)
			{
				return !IsInitialFrameStall(ex);
			}
			return false;
		}
		return true;
	}

	internal static bool IsInitialFrameStall(Exception ex)
	{
		if (ex is CapturePipelineStallException ex2)
		{
			return ex2.Stage == "initial_frame";
		}
		return false;
	}

	public static void Record(string displayKey, string source, FailureClass cls, string sourceTag, Exception ex)
	{
		Record(displayKey, source, cls, sourceTag, ex, DateTimeOffset.UtcNow);
	}

	internal static void Record(string displayKey, string source, FailureClass cls, string sourceTag, Exception ex, DateTimeOffset nowUtc)
	{
		if (IsNeverRecorded(ex))
		{
			return;
		}
		if (cls == FailureClass.Starvation)
		{
			StrikeState strikeState = AddStrike(DePreferences, CompositeKey(source, displayKey), nowUtc);
			KuroLog.Capture($"capture_health transition=depreference_set source={source} display={displayKey} class={ClassName(cls)} count={strikeState.Count}", "Record", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\CaptureHealthRegistry.cs", 209);
			return;
		}
		AddStrike(FailureCounts, ClassName(cls) + "|" + CompositeKey(source, displayKey), nowUtc);
		if (cls == FailureClass.HardInit && source == "dxgi" && RemoteAttachedSource())
		{
			int epoch = EnvironmentEpochSource();
			RemoteDxgiState remoteDxgiState = RemoteDxgiHardInits.AddOrUpdate(displayKey, new RemoteDxgiState(1, epoch, nowUtc), (string _, RemoteDxgiState current) => (current.Epoch != epoch) ? new RemoteDxgiState(1, epoch, nowUtc) : new RemoteDxgiState(current.Count + 1, epoch, nowUtc));
			KuroLog.Capture($"capture_health transition=depreference_set source={"dxgi"} display={displayKey} class={ClassName(cls)} count={remoteDxgiState.Count} epoch={epoch}", "Record", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\CaptureHealthRegistry.cs", 226);
		}
	}

	internal static int RecentFailureCount(string displayKey, string source, FailureClass cls, DateTimeOffset nowUtc)
	{
		if (!FailureCounts.TryGetValue(ClassName(cls) + "|" + CompositeKey(source, displayKey), out var value) || !(nowUtc - value.LastUtc <= DecayWindow))
		{
			return 0;
		}
		return value.Count;
	}

	public static void RecordFirstFrame(string displayKey, string source)
	{
		RecordFirstFrame(displayKey, source, DateTimeOffset.UtcNow);
	}

	internal static void RecordFirstFrame(string displayKey, string source, DateTimeOffset nowUtc)
	{
		DisplayHealth orAdd = Displays.GetOrAdd(displayKey, (string _) => new DisplayHealth());
		Volatile.Write(ref orAdd.LastFrameUtcTicks, nowUtc.UtcTicks);
		if (orAdd.FastPath)
		{
			return;
		}
		lock (orAdd)
		{
			orAdd.EverProducedFrame = true;
			orAdd.LastFrameSource = source;
			orAdd.ChronicRun = 0;
			orAdd.RemoteRun = 0;
			orAdd.StructuralEvidence.Clear();
			orAdd.FastPath = true;
		}
	}

	internal static bool HasEverProducedFrame(string displayKey)
	{
		if (Displays.TryGetValue(displayKey, out DisplayHealth value))
		{
			return value.EverProducedFrame;
		}
		return false;
	}

	internal static DateTimeOffset? LastFrameUtc(string displayKey)
	{
		if (Displays.TryGetValue(displayKey, out DisplayHealth value))
		{
			long num = Volatile.Read(in value.LastFrameUtcTicks);
			if (num > 0)
			{
				return new DateTimeOffset(num, TimeSpan.Zero);
			}
		}
		return null;
	}

	public static void RecordLadderExhaustion(string displayKey, SessionEnvironmentKind sessionEnvironmentKind)
	{
		RecordLadderExhaustion(displayKey, sessionEnvironmentKind, DateTimeOffset.UtcNow);
	}

	internal static void RecordLadderExhaustion(string displayKey, SessionEnvironmentKind sessionEnvironmentKind, DateTimeOffset nowUtc)
	{
		DisplayHealth orAdd = Displays.GetOrAdd(displayKey, (string _) => new DisplayHealth());
		lock (orAdd)
		{
			orAdd.FastPath = false;
			orAdd.ChronicRun++;
			orAdd.RemoteRun = ((sessionEnvironmentKind == SessionEnvironmentKind.RemoteAttached) ? (orAdd.RemoteRun + 1) : 0);
			orAdd.LastExhaustionUtcTicks = nowUtc.UtcTicks;
			if (orAdd.StructuralEvidence.Count >= 128)
			{
				orAdd.StructuralEvidence.RemoveAt(0);
			}
			orAdd.StructuralEvidence.Add(nowUtc);
		}
	}

	public static void ClearForRecoveryEvent(CaptureRecoveryEvent reason)
	{
		foreach (DisplayHealth value in Displays.Values)
		{
			lock (value)
			{
				switch (reason)
				{
				case CaptureRecoveryEvent.TopologyChange:
					value.ChronicRun = 0;
					value.RemoteRun = 0;
					value.StructuralEvidence.Clear();
					break;
				case CaptureRecoveryEvent.Unlock:
				case CaptureRecoveryEvent.Logon:
				case CaptureRecoveryEvent.Resume:
					value.ChronicRun = 0;
					value.RemoteRun = 0;
					break;
				case CaptureRecoveryEvent.FirstUserInput:
					value.ChronicRun = 0;
					value.StructuralEvidence.Clear();
					break;
				}
			}
		}
	}

	public static SessionPacingClass DerivePacing(IReadOnlyList<string> displayKeys, SessionEnvironmentKind sessionEnvironmentKind)
	{
		return DerivePacing(displayKeys, sessionEnvironmentKind, DateTimeOffset.UtcNow);
	}

	internal static SessionPacingClass DerivePacing(IReadOnlyList<string> displayKeys, SessionEnvironmentKind sessionEnvironmentKind, DateTimeOffset nowUtc)
	{
		SessionPacingClass sessionPacingClass = DerivePacingCore(displayKeys, sessionEnvironmentKind, nowUtc);
		SessionPacingClass sessionPacingClass2 = (SessionPacingClass)Interlocked.Exchange(ref _lastPacing, (int)sessionPacingClass);
		if (sessionPacingClass2 != sessionPacingClass)
		{
			if (sessionPacingClass2 != SessionPacingClass.None)
			{
				KuroLog.Capture($"capture_health transition=pacing_exit class={PacingName(sessionPacingClass2)} displays={displayKeys.Count}", "DerivePacing", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\CaptureHealthRegistry.cs", 373);
			}
			if (sessionPacingClass != SessionPacingClass.None)
			{
				KuroLog.Capture($"capture_health transition=pacing_enter class={PacingName(sessionPacingClass)} displays={displayKeys.Count}", "DerivePacing", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\CaptureHealthRegistry.cs", 378);
			}
		}
		return sessionPacingClass;
	}

	private static SessionPacingClass DerivePacingCore(IReadOnlyList<string> displayKeys, SessionEnvironmentKind sessionEnvironmentKind, DateTimeOffset nowUtc)
	{
		if (displayKeys.Count == 0)
		{
			return SessionPacingClass.None;
		}
		bool flag = true;
		bool flag2 = true;
		bool flag3 = false;
		bool flag4 = false;
		foreach (string displayKey in displayKeys)
		{
			if (!Displays.TryGetValue(displayKey, out DisplayHealth value))
			{
				flag2 = false;
				continue;
			}
			lock (value)
			{
				if (value.EverProducedFrame)
				{
					flag = false;
				}
				bool flag5 = nowUtc - new DateTimeOffset(value.LastExhaustionUtcTicks, TimeSpan.Zero) <= DecayWindow;
				if (flag5 && value.ChronicRun >= 5)
				{
					flag3 = true;
				}
				if (flag5 && value.RemoteRun >= 2)
				{
					flag4 = true;
				}
				if (!HasStructuralEvidence(value.StructuralEvidence))
				{
					flag2 = false;
				}
			}
		}
		if ((sessionEnvironmentKind == SessionEnvironmentKind.Console) & flag & flag2)
		{
			return SessionPacingClass.StructuralDead;
		}
		if ((sessionEnvironmentKind == SessionEnvironmentKind.RemoteAttached) & flag4)
		{
			return SessionPacingClass.RemoteExhausted;
		}
		if (!flag3)
		{
			return SessionPacingClass.None;
		}
		return SessionPacingClass.Chronic;
	}

	private static bool HasStructuralEvidence(List<DateTimeOffset> evidence)
	{
		if (evidence.Count < 3)
		{
			return false;
		}
		long num = long.MaxValue;
		long num2 = long.MinValue;
		foreach (DateTimeOffset item in evidence)
		{
			long val = item.UtcTicks / DecayWindow.Ticks;
			num = Math.Min(num, val);
			num2 = Math.Max(num2, val);
		}
		return num2 - num >= 2;
	}

	public static IReadOnlyList<SourcePlan> ResolveStartOrder(string displayKey, RecorderCoreKind core)
	{
		return ResolveStartOrder(displayKey, core, DateTimeOffset.UtcNow);
	}

	internal static IReadOnlyList<SourcePlan> ResolveStartOrder(string displayKey, RecorderCoreKind core, DateTimeOffset nowUtc)
	{
		List<SourcePlan> list = new List<SourcePlan>(4);
		if (core == RecorderCoreKind.GpuV2)
		{
			list.Add(new SourcePlan("gpu_v2", "core_gpu_v2"));
		}
		bool flag = WgcSupportedSource();
		string text = (flag ? BorderSuppressionBlockerSource() : null);
		List<SourcePlan> list2 = new List<SourcePlan>(3);
		List<SourcePlan> list3 = new List<SourcePlan>(3);
		if (TryGetDePreference("dxgi", displayKey, nowUtc, out string reason))
		{
			list3.Add(new SourcePlan("dxgi", reason));
		}
		else if (RemoteDxgiDePreferenceActive(displayKey))
		{
			list3.Add(new SourcePlan("dxgi", "depreferred_structural_remote_dxgi"));
		}
		else
		{
			list2.Add(new SourcePlan("dxgi", "baseline"));
		}
		if (flag && text == null)
		{
			Place("wgc_borderless", "baseline", displayKey, nowUtc, list2, list3);
		}
		if (flag)
		{
			Place("wgc_bordered", (text == null) ? "baseline" : ("borderless_unavailable_" + text), displayKey, nowUtc, list2, list3);
		}
		list.AddRange(list2);
		list.AddRange(list3);
		return list;
	}

	private static void Place(string source, string baselineReason, string displayKey, DateTimeOffset nowUtc, List<SourcePlan> baseline, List<SourcePlan> dePreferred)
	{
		if (TryGetDePreference(source, displayKey, nowUtc, out string reason))
		{
			dePreferred.Add(new SourcePlan(source, reason));
		}
		else
		{
			baseline.Add(new SourcePlan(source, baselineReason));
		}
	}

	private static bool TryGetDePreference(string source, string displayKey, DateTimeOffset nowUtc, out string reason)
	{
		string key = CompositeKey(source, displayKey);
		if (DePreferences.TryGetValue(key, out var value))
		{
			if (!(nowUtc - value.LastUtc > DecayWindow))
			{
				reason = "depreferred_starvation";
				return true;
			}
			if (DePreferences.TryRemove(key, out var value2))
			{
				KuroLog.Capture($"capture_health transition=depreference_decayed source={source} display={displayKey} class=starvation count={value2.Count}", "TryGetDePreference", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\CaptureHealthRegistry.cs", 566);
			}
		}
		reason = "";
		return false;
	}

	private static bool RemoteDxgiDePreferenceActive(string displayKey)
	{
		if (!RemoteDxgiHardInits.TryGetValue(displayKey, out var value))
		{
			return false;
		}
		if (value.Epoch != EnvironmentEpochSource())
		{
			if (RemoteDxgiHardInits.TryRemove(displayKey, out var value2))
			{
				KuroLog.Capture($"capture_health transition=depreference_decayed source={"dxgi"} display={displayKey} class=hard_init count={value2.Count}", "RemoteDxgiDePreferenceActive", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\CaptureHealthRegistry.cs", 597);
			}
			return false;
		}
		return true;
	}

	public static void RecordGen2TierFailure(string displayKey, string frontEnd)
	{
		RecordGen2TierFailure(displayKey, frontEnd, DateTimeOffset.UtcNow);
	}

	internal static void RecordGen2TierFailure(string displayKey, string frontEnd, DateTimeOffset nowUtc)
	{
		StrikeState strikeState = AddStrike(Gen2TierStrikes, CompositeKey(frontEnd, displayKey), nowUtc);
		if (strikeState.Count == 3)
		{
			KuroLog.Capture($"capture_health transition=gen2_demoted source={frontEnd} display={displayKey} count={strikeState.Count} (window {DecayWindow.TotalMinutes:F0}m)", "RecordGen2TierFailure", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\CaptureHealthRegistry.cs", 626);
		}
	}

	public static bool IsGen2TierDemoted(string displayKey, string frontEnd, out string reason)
	{
		return IsGen2TierDemoted(displayKey, frontEnd, DateTimeOffset.UtcNow, out reason);
	}

	internal static bool IsGen2TierDemoted(string displayKey, string frontEnd, DateTimeOffset nowUtc, out string reason)
	{
		string key = CompositeKey(frontEnd, displayKey);
		if (!Gen2TierStrikes.TryGetValue(key, out var value))
		{
			reason = "";
			return false;
		}
		if (nowUtc - value.LastUtc > DecayWindow)
		{
			if (Gen2TierStrikes.TryRemove(key, out var value2) && value2.Count >= 3)
			{
				KuroLog.Capture($"capture_health transition=gen2_demotion_decayed source={frontEnd} display={displayKey} count={value2.Count}", "IsGen2TierDemoted", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\CaptureHealthRegistry.cs", 651);
			}
			reason = "";
			return false;
		}
		if (value.Count >= 3)
		{
			reason = $"recent_{frontEnd}_failures_count_{value.Count}";
			return true;
		}
		reason = "";
		return false;
	}

	public static bool AllGen2TiersDemoted(string displayKey, out string reason)
	{
		return AllGen2TiersDemoted(displayKey, DateTimeOffset.UtcNow, out reason);
	}

	internal static bool AllGen2TiersDemoted(string displayKey, DateTimeOffset nowUtc, out string reason)
	{
		string text = BorderSuppressionBlockerSource();
		bool flag = text != null;
		bool flag2 = IsGen2TierDemoted(displayKey, "wgc_borderless", nowUtc, out string reason2);
		if (IsGen2TierDemoted(displayKey, "dda_gpu", nowUtc, out string reason3) && (flag | flag2) && IsGen2TierDemoted(displayKey, "wgc_bordered", nowUtc, out string reason4))
		{
			if (flag)
			{
				reason2 = "wgc_borderless_unavailable_" + text;
			}
			reason = $"all_gpu_frontends_demoted_{reason3}_{reason2}_{reason4}";
			return true;
		}
		reason = "";
		return false;
	}

	internal static string ClassName(FailureClass cls)
	{
		return cls switch
		{
			FailureClass.HardInit => "hard_init", 
			FailureClass.Runtime => "runtime", 
			FailureClass.NoFirstFrame => "no_first_frame", 
			FailureClass.Starvation => "starvation", 
			FailureClass.TopologyBlip => "topology_blip", 
			_ => "unknown", 
		};
	}

	internal static string PacingName(SessionPacingClass pacing)
	{
		return pacing switch
		{
			SessionPacingClass.StructuralDead => "structural_dead", 
			SessionPacingClass.Chronic => "chronic", 
			SessionPacingClass.RemoteExhausted => "remote_exhausted", 
			_ => "none", 
		};
	}

	private static StrikeState AddStrike(ConcurrentDictionary<string, StrikeState> strikes, string key, DateTimeOffset nowUtc)
	{
		return strikes.AddOrUpdate(key, new StrikeState(1, nowUtc), (string _, StrikeState current) => (!(nowUtc - current.LastUtc > DecayWindow)) ? new StrikeState(current.Count + 1, nowUtc) : new StrikeState(1, nowUtc));
	}

	private static string CompositeKey(string source, string displayKey)
	{
		return source + "|" + displayKey;
	}

	internal static void ResetForTests()
	{
		Gen2TierStrikes.Clear();
		DePreferences.Clear();
		FailureCounts.Clear();
		RemoteDxgiHardInits.Clear();
		Displays.Clear();
		Volatile.Write(ref _lastPacing, 0);
		EnvironmentEpochSource = () => RemoteSessionProbe.EnvironmentEpoch;
		RemoteAttachedSource = () => RemoteSessionProbe.Kind == SessionEnvironmentKind.RemoteAttached;
		WgcSupportedSource = () => WgcMonitorCaptureBackend.IsSupported();
		BorderSuppressionBlockerSource = () => WgcMonitorCaptureBackend.BorderSuppressionBlocker;
	}
}
