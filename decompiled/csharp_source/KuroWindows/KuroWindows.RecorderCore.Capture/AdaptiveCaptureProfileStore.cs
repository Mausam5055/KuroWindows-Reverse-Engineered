using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace KuroWindows.RecorderCore.Capture;

internal sealed class AdaptiveCaptureProfileStore
{
	private sealed class Entry
	{
		public int ProfileIndex { get; set; }

		public int ConsecutiveFailures { get; set; }

		public string? LastDowngradeReason { get; set; }
	}

	private const int FailureThreshold = 3;

	private const int MaxProfileIndex = 5;

	private readonly object _sync = new object();

	private readonly Dictionary<string, Entry> _topologies = new Dictionary<string, Entry>();

	public AdaptiveCaptureProfileStore()
	{
		DeleteLegacyStateFile();
	}

	public AdaptiveCaptureProfileSelection GetSelection(string topologySignature)
	{
		lock (_sync)
		{
			if (!_topologies.TryGetValue(StableKey(topologySignature), out Entry value))
			{
				return new AdaptiveCaptureProfileSelection(DisplayTopologyPolicy.ProfileForIndex(0), "default", 0);
			}
			return new AdaptiveCaptureProfileSelection(DisplayTopologyPolicy.ProfileForIndex(Math.Clamp(value.ProfileIndex, 0, 5)), value.LastDowngradeReason ?? ((value.ProfileIndex > 0) ? "sticky_local_fallback" : "default"), value.ConsecutiveFailures);
		}
	}

	public AdaptiveCaptureProfile RecordFailure(DisplayTopologyPolicyResult policy, string reason)
	{
		lock (_sync)
		{
			string text = StableKey(policy.TopologySignature);
			if (!_topologies.TryGetValue(text, out Entry value))
			{
				value = new Entry();
				_topologies[text] = value;
			}
			if (value.ProfileIndex != policy.Profile.Index)
			{
				value.ProfileIndex = policy.Profile.Index;
				value.ConsecutiveFailures = 0;
			}
			value.ConsecutiveFailures++;
			if (value.ConsecutiveFailures >= 3 && value.ProfileIndex < 5)
			{
				value.ProfileIndex++;
				value.ConsecutiveFailures = 0;
				value.LastDowngradeReason = reason;
				KuroLog.Capture($"Adaptive capture profile downgraded topology={text} profile={value.ProfileIndex} reason={reason}", "RecordFailure", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\RecorderCore\\Capture\\AdaptiveCaptureProfileStore.cs", 75);
			}
			else
			{
				KuroLog.Capture($"Adaptive capture failure recorded topology={text} profile={value.ProfileIndex} failures={value.ConsecutiveFailures}/{3} reason={reason}", "RecordFailure", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\RecorderCore\\Capture\\AdaptiveCaptureProfileStore.cs", 81);
			}
			return DisplayTopologyPolicy.ProfileForIndex(value.ProfileIndex);
		}
	}

	public void MarkStable(DisplayTopologyPolicyResult policy)
	{
		lock (_sync)
		{
			string text = StableKey(policy.TopologySignature);
			if (_topologies.TryGetValue(text, out Entry value) && value.ConsecutiveFailures != 0)
			{
				KuroLog.Capture($"Adaptive capture failures cleared topology={text} profile={value.ProfileIndex} previous_failures={value.ConsecutiveFailures}", "MarkStable", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\RecorderCore\\Capture\\AdaptiveCaptureProfileStore.cs", 97);
				value.ConsecutiveFailures = 0;
			}
		}
	}

	public void ResetForNewRecordingSession()
	{
		lock (_sync)
		{
			if (_topologies.Count != 0)
			{
				int num = Enumerable.Count(_topologies.Values, (Entry entry) => entry.ProfileIndex > 0);
				_topologies.Clear();
				KuroLog.Capture($"Adaptive capture profile state reset for new recording session (cleared {num} downgraded topology entr{((num == 1) ? "y" : "ies")})", "ResetForNewRecordingSession", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\RecorderCore\\Capture\\AdaptiveCaptureProfileStore.cs", 120);
			}
		}
	}

	private static string StableKey(string topologySignature)
	{
		return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(topologySignature))).Substring(0, 16).ToLowerInvariant();
	}

	private static void DeleteLegacyStateFile()
	{
		try
		{
			string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppConfig.LocalDataFolderName, "recorder_path_state.json");
			if (File.Exists(path))
			{
				File.Delete(path);
			}
		}
		catch
		{
		}
	}
}
