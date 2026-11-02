using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using KuroWindows.Input;

namespace KuroWindows.Diagnostics;

internal static class HardwareDiagnostics
{
	private struct MEMORYSTATUSEX
	{
		public uint dwLength;

		public uint dwMemoryLoad;

		public ulong ullTotalPhys;

		public ulong ullAvailPhys;

		public ulong ullTotalPageFile;

		public ulong ullAvailPageFile;

		public ulong ullTotalVirtual;

		public ulong ullAvailVirtual;

		public ulong ullAvailExtendedVirtual;
	}

	private static int _started;

	public static void LogSnapshotInBackground()
	{
		if (Interlocked.Exchange(ref _started, 1) == 0)
		{
			Task.Run((Action)LogSnapshot);
		}
	}

	private static void LogSnapshot()
	{
		try
		{
			long totalPhysicalMemoryBytes = GetTotalPhysicalMemoryBytes();
			long value = ((totalPhysicalMemoryBytes > 0) ? ((long)Math.Round((double)totalPhysicalMemoryBytes / 1024.0 / 1024.0 / 1024.0)) : 0);
			IReadOnlyList<MonitorTable.Entry> screens = MonitorTable.Refresh().Screens;
			string value2 = string.Join("|", screens.Select((MonitorTable.Entry s) => DescribeDisplay(s.Screen)));
			string value3 = string.Join("|", QueryVideoControllers().Take(6));
			string value4 = FirstWmiValue("Win32_Processor", "Name") ?? "unknown";
			string value5 = FirstWmiValue("Win32_ComputerSystem", "Model") ?? "unknown";
			KuroLog.SystemEvent("Hardware snapshot os=\"" + Clean($"Windows {Environment.OSVersion.Version}") + "\" machine_model=\"" + Clean(value5) + "\" cpu=\"" + Clean(value4) + "\" " + $"logical_processors={Environment.ProcessorCount} " + $"physical_memory_gb={value} " + $"display_count={screens.Count} " + "displays=\"" + Clean(value2) + "\" gpus=\"" + Clean(value3) + "\"", "LogSnapshot", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Diagnostics\\HardwareDiagnostics.cs", 35);
		}
		catch (Exception ex)
		{
			KuroLog.SystemEvent("Hardware snapshot failed: " + ex.Message, "LogSnapshot", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Diagnostics\\HardwareDiagnostics.cs", 48);
		}
	}

	private static IEnumerable<string> QueryVideoControllers()
	{
		ManagementObjectSearcher searcher = null;
		try
		{
			searcher = new ManagementObjectSearcher("SELECT Name,DriverVersion,AdapterRAM,VideoProcessor FROM Win32_VideoController");
			foreach (ManagementObject item in searcher.Get())
			{
				using (item)
				{
					string value = item["Name"]?.ToString();
					if (!string.IsNullOrWhiteSpace(value))
					{
						string text = item["DriverVersion"]?.ToString();
						string text2 = item["VideoProcessor"]?.ToString();
						long num = AdapterRamMb(item["AdapterRAM"]);
						yield return $"name={Clean(value)},driver={Clean(text ?? "-")},vram_mb={((num > 0) ? num.ToString() : "-")},processor={Clean(text2 ?? "-")}";
					}
				}
			}
		}
		finally
		{
			searcher?.Dispose();
		}
	}

	internal static string? FirstWmiValue(string className, string propertyName)
	{
		try
		{
			using ManagementObjectSearcher managementObjectSearcher = new ManagementObjectSearcher("SELECT " + propertyName + " FROM " + className);
			foreach (ManagementObject item in managementObjectSearcher.Get())
			{
				using (item)
				{
					string text = item[propertyName]?.ToString();
					if (!string.IsNullOrWhiteSpace(text))
					{
						return text;
					}
				}
			}
		}
		catch
		{
		}
		return null;
	}

	private static string DescribeDisplay(Screen screen)
	{
		Rectangle bounds = screen.Bounds;
		return $"device={Clean(screen.DeviceName)},primary={screen.Primary},bounds={bounds.Width}x{bounds.Height}@{bounds.X},{bounds.Y}";
	}

	private static long AdapterRamMb(object? value)
	{
		try
		{
			if (value == null)
			{
				return 0L;
			}
			long num = Convert.ToInt64(value);
			return (num > 0) ? (num / 1024 / 1024) : 0;
		}
		catch
		{
			return 0L;
		}
	}

	private static string Clean(string value)
	{
		string text = value.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ')
			.Replace('"', '\'')
			.Trim();
		while (text.Contains("  ", StringComparison.Ordinal))
		{
			text = text.Replace("  ", " ");
		}
		if (text.Length > 180)
		{
			return text.Substring(0, 180) + "...";
		}
		return text;
	}

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);

	private static long GetTotalPhysicalMemoryBytes()
	{
		MEMORYSTATUSEX buffer = new MEMORYSTATUSEX
		{
			dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>()
		};
		if (!GlobalMemoryStatusEx(ref buffer))
		{
			return 0L;
		}
		return (long)buffer.ullTotalPhys;
	}
}
