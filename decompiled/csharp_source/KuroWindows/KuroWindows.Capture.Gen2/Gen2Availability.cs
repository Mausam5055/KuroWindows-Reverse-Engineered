using System;
using System.Collections.Generic;
using KuroWindows.RecorderCore.Capture;

namespace KuroWindows.Capture.Gen2;

internal static class Gen2Availability
{
	public static bool TryProbe(IReadOnlyList<CaptureDisplaySettings> displays, out string unavailableReason)
	{
		string environmentVariable = Environment.GetEnvironmentVariable("KURO_GEN2_FORCE_UNAVAILABLE");
		if (!string.IsNullOrWhiteSpace(environmentVariable))
		{
			unavailableReason = "forced_unavailable_" + environmentVariable.Trim().ToLowerInvariant();
			return false;
		}
		int num = 0;
		List<string> list = new List<string>();
		foreach (CaptureDisplaySettings display in displays)
		{
			if (CaptureHealthRegistry.AllGen2TiersDemoted(CaptureHealthRegistry.KeyFor(display.Screen), out string reason))
			{
				list.Add($"display={display.LogicalDisplayIndex}_{reason}");
				continue;
			}
			if (!GpuV2CaptureBackend.IsNativeResolution(display))
			{
				list.Add($"gen2_requires_native_resolution display={display.LogicalDisplayIndex} capture={display.CaptureWidth}x{display.CaptureHeight} native={display.NativeBounds.Width}x{display.NativeBounds.Height}");
				continue;
			}
			if (!GpuEncoderContext.TryCreate(display.Screen, display.CaptureWidth & -2, display.CaptureHeight & -2, out GpuEncoderContext gpuContext, out string failureReason))
			{
				list.Add($"display={display.LogicalDisplayIndex}_{failureReason}");
				continue;
			}
			try
			{
				if (!TextureVideoEncoder.TryProbeSinkWriter(gpuContext, display.LogicalDisplayIndex, display.CaptureWidth & -2, display.CaptureHeight & -2, display.CaptureFps, out string failureReason2))
				{
					list.Add($"display={display.LogicalDisplayIndex}_{failureReason2}");
				}
				else
				{
					num++;
				}
			}
			finally
			{
				gpuContext.ReleaseUser();
			}
		}
		if (num > 0)
		{
			if (list.Count > 0)
			{
				KuroLog.Capture($"gpu_v2 partial availability gpu_displays={num}/{displays.Count} cpu_fallback_displays=[{string.Join("; ", list)}]", "TryProbe", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\Gen2\\Gen2Availability.cs", 96);
			}
			unavailableReason = "";
			return true;
		}
		unavailableReason = ((list.Count > 0) ? string.Join("; ", list) : "no_displays");
		return false;
	}
}
