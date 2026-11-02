using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ClientTelemetry;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;

namespace KuroWindows.Diagnostics;

internal static class WebView2Health
{
	private sealed record RepairMarker(DateTimeOffset AttemptedAt, string? RegisteredVersion);

	private const string BootstrapperUrl = "https://go.microsoft.com/fwlink/p/?LinkId=2124703";

	private const string RuntimeClientsKey = "SOFTWARE\\WOW6432Node\\Microsoft\\EdgeUpdate\\Clients\\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";

	private const string RuntimeClientsKeyUser = "Software\\Microsoft\\EdgeUpdate\\Clients\\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";

	private static readonly string MarkerPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppConfig.LocalDataFolderName, "diagnostics", "webview2-repair.json");

	private static int _autoRepairStarted;

	private static int _userRepairStarted;

	public static WebView2HealthProbe Probe()
	{
		string value = null;
		try
		{
			value = CoreWebView2Environment.GetAvailableBrowserVersionString();
		}
		catch
		{
		}
		string text = null;
		string text2 = null;
		try
		{
			using RegistryKey registryKey = Registry.LocalMachine.OpenSubKey("SOFTWARE\\WOW6432Node\\Microsoft\\EdgeUpdate\\Clients\\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}");
			using RegistryKey registryKey2 = ((registryKey == null) ? Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\EdgeUpdate\\Clients\\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}") : null);
			RegistryKey registryKey3 = registryKey ?? registryKey2;
			text = registryKey3?.GetValue("pv") as string;
			text2 = (registryKey3?.GetValue("location") as string) ?? (registryKey3?.GetValue("InstallLocation") as string);
		}
		catch
		{
		}
		bool browserExecutableExists = false;
		try
		{
			if (!string.IsNullOrEmpty(text) && !string.IsNullOrEmpty(text2))
			{
				browserExecutableExists = File.Exists(Path.Combine(text2, text, "msedgewebview2.exe")) || File.Exists(Path.Combine(text2, "msedgewebview2.exe"));
			}
		}
		catch
		{
		}
		string value2 = null;
		try
		{
			value2 = Environment.GetEnvironmentVariable("WEBVIEW2_BROWSER_EXECUTABLE_FOLDER");
			if (string.IsNullOrEmpty(value2))
			{
				using RegistryKey registryKey4 = Registry.LocalMachine.OpenSubKey("Software\\Policies\\Microsoft\\Edge\\WebView2\\BrowserExecutableFolder") ?? Registry.CurrentUser.OpenSubKey("Software\\Policies\\Microsoft\\Edge\\WebView2\\BrowserExecutableFolder");
				if (registryKey4 != null)
				{
					string name = Path.GetFileName(Environment.ProcessPath) ?? "";
					value2 = (registryKey4.GetValue(name) as string) ?? (registryKey4.GetValue("*") as string);
				}
			}
		}
		catch
		{
		}
		return new WebView2HealthProbe(NullIfEmpty(value), NullIfEmpty(text), NullIfEmpty(text2), browserExecutableExists, NullIfEmpty(value2));
	}

	public static string Describe(WebView2HealthProbe probe)
	{
		return $"loader_version={probe.LoaderVersion ?? "none"} registered_version={probe.RegisteredVersion ?? "none"} location={probe.RegisteredLocation ?? "-"} exe_present={probe.BrowserExecutableExists} override={probe.FolderOverride ?? "-"}";
	}

	public static WebView2FailureClass Classify(Exception ex)
	{
		if (ex is WebView2RuntimeNotFoundException)
		{
			return WebView2FailureClass.RuntimeMissing;
		}
		WebView2FailureClass result;
		switch (ex.HResult)
		{
		case -2147467259:
		case -2147024894:
		case -2147023282:
			result = WebView2FailureClass.RuntimeBroken;
			break;
		case -2147024891:
		case -2147024816:
		case -2147024784:
		case -2147019873:
			result = WebView2FailureClass.UserDataFolder;
			break;
		default:
			result = WebView2FailureClass.Unknown;
			break;
		}
		return result;
	}

	public static Task<bool> TryAutoRepairAsync()
	{
		if (Interlocked.Exchange(ref _autoRepairStarted, 1) != 0)
		{
			KuroLog.LocalOnly("app", "WebView2 auto-repair skipped: already attempted this run", "TryAutoRepairAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Diagnostics\\WebView2Health.cs", 183);
			return Task.FromResult(result: false);
		}
		WebView2HealthProbe probe = Probe();
		if (!AutoRepairAllowed(probe))
		{
			return Task.FromResult(result: false);
		}
		return RepairCoreAsync(probe, elevate: false, "auto");
	}

	public static Task<bool> TryUserRepairAsync()
	{
		if (Interlocked.Exchange(ref _userRepairStarted, 1) != 0)
		{
			KuroLog.LocalOnly("app", "WebView2 user repair skipped: already attempted this run", "TryUserRepairAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Diagnostics\\WebView2Health.cs", 207);
			return Task.FromResult(result: false);
		}
		WebView2HealthProbe webView2HealthProbe = Probe();
		return RepairCoreAsync(webView2HealthProbe, webView2HealthProbe.LoaderVersion != null, "user");
	}

	private static async Task<bool> RepairCoreAsync(WebView2HealthProbe probe, bool elevate, string kind)
	{
		KuroLog.App($"WebView2 repair starting kind={kind} elevated={elevate} ({Describe(probe)})", "RepairCoreAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Diagnostics\\WebView2Health.cs", 217);
		string setupPath = Path.Combine(Path.GetTempPath(), $"KuroWebView2Setup-{Guid.NewGuid():N}.exe");
		using CancellationTokenSource deadline = new CancellationTokenSource(TimeSpan.FromMinutes(8.0));
		_ = 5;
		try
		{
			using (HttpClient http = new HttpClient())
			{
				await using Stream download = await http.GetStreamAsync("https://go.microsoft.com/fwlink/p/?LinkId=2124703", deadline.Token).ConfigureAwait(continueOnCapturedContext: false);
				await using FileStream file = File.Create(setupPath);
				await download.CopyToAsync(file, deadline.Token).ConfigureAwait(continueOnCapturedContext: false);
			}
			WriteMarker(new RepairMarker(DateTimeOffset.UtcNow, probe.RegisteredVersion));
			ProcessStartInfo processStartInfo = new ProcessStartInfo(setupPath, "/silent /install")
			{
				UseShellExecute = true
			};
			if (elevate)
			{
				processStartInfo.Verb = "runas";
			}
			using Process process = Process.Start(processStartInfo);
			if (process == null)
			{
				KuroLog.App("WebView2 repair failed: bootstrapper process did not start", "RepairCoreAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Diagnostics\\WebView2Health.cs", 254);
				Telemetry.Emit(Ev.WebviewRepair(kind, RepairOutcome.Failed, null, "bootstrapper process did not start"));
				return false;
			}
			await process.WaitForExitAsync(deadline.Token).ConfigureAwait(continueOnCapturedContext: false);
			string bootstrapperNote = ((process.ExitCode == 0) ? null : $"bootstrapper exit_code=0x{process.ExitCode:X8}");
			if (bootstrapperNote != null)
			{
				KuroLog.App("WebView2 repair " + bootstrapperNote, "RepairCoreAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Diagnostics\\WebView2Health.cs", 266);
			}
			while (!deadline.IsCancellationRequested)
			{
				WebView2HealthProbe webView2HealthProbe = Probe();
				if ((object)webView2HealthProbe != null && webView2HealthProbe.IsHealthy)
				{
					KuroLog.App("WebView2 repair succeeded kind=" + kind + " runtime_version=" + webView2HealthProbe.LoaderVersion, "RepairCoreAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Diagnostics\\WebView2Health.cs", 276);
					Telemetry.Emit(Ev.WebviewRepair(kind, RepairOutcome.Succeeded, webView2HealthProbe.LoaderVersion, bootstrapperNote));
					return true;
				}
				await Task.Delay(TimeSpan.FromSeconds(3.0), deadline.Token).ConfigureAwait(continueOnCapturedContext: false);
			}
			KuroLog.App("WebView2 repair failed: runtime still unhealthy after install wait (" + Describe(Probe()) + ")", "RepairCoreAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Diagnostics\\WebView2Health.cs", 284);
			Telemetry.Emit(Ev.WebviewRepair(kind, RepairOutcome.Failed, null, (bootstrapperNote == null) ? "runtime still unhealthy after install wait" : ("runtime still unhealthy after install wait; " + bootstrapperNote)));
			return false;
		}
		catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
		{
			KuroLog.App("WebView2 repair failed: elevation declined by user", "RepairCoreAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Diagnostics\\WebView2Health.cs", 295);
			Telemetry.Emit(Ev.WebviewRepair(kind, RepairOutcome.Failed, null, "elevation declined"));
			return false;
		}
		catch (OperationCanceledException)
		{
			KuroLog.App("WebView2 repair failed: timed out (" + Describe(Probe()) + ")", "RepairCoreAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Diagnostics\\WebView2Health.cs", 301);
			Telemetry.Emit(Ev.WebviewRepair(kind, RepairOutcome.Failed, null, "timed out"));
			return false;
		}
		catch (Exception ex3)
		{
			KuroLog.App("WebView2 repair failed: " + ExceptionDescriber.Describe(ex3), "RepairCoreAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Diagnostics\\WebView2Health.cs", 307);
			Telemetry.Emit(Ev.WebviewRepair(kind, RepairOutcome.Failed, null, ExceptionDescriber.Describe(ex3)));
			return false;
		}
		finally
		{
			try
			{
				File.Delete(setupPath);
			}
			catch
			{
			}
		}
	}

	public static async Task CheckAndAutoRepairInBackgroundAsync()
	{
		_ = 1;
		try
		{
			await Task.Delay(TimeSpan.FromSeconds(45.0)).ConfigureAwait(continueOnCapturedContext: false);
			WebView2HealthProbe webView2HealthProbe = Probe();
			if (!webView2HealthProbe.IsHealthy)
			{
				KuroLog.App("WebView2 runtime unhealthy at startup; dashboard and staged updates may be blocked until repaired (" + Describe(webView2HealthProbe) + ")", "CheckAndAutoRepairInBackgroundAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Diagnostics\\WebView2Health.cs", 336);
				if (webView2HealthProbe.LoaderVersion == null)
				{
					await TryAutoRepairAsync().ConfigureAwait(continueOnCapturedContext: false);
				}
			}
		}
		catch (Exception ex)
		{
			KuroLog.LocalOnly("app", "WebView2 startup health check error: " + ex.Message, "CheckAndAutoRepairInBackgroundAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Diagnostics\\WebView2Health.cs", 344);
		}
	}

	private static bool AutoRepairAllowed(WebView2HealthProbe probe)
	{
		try
		{
			if (File.Exists(MarkerPath))
			{
				RepairMarker repairMarker = JsonSerializer.Deserialize<RepairMarker>(File.ReadAllText(MarkerPath));
				if (repairMarker != null && DateTimeOffset.UtcNow - repairMarker.AttemptedAt < TimeSpan.FromHours(24.0) && repairMarker.RegisteredVersion == probe.RegisteredVersion)
				{
					KuroLog.LocalOnly("app", "WebView2 auto-repair skipped: attempted within 24h for same runtime version", "AutoRepairAllowed", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Diagnostics\\WebView2Health.cs", 362);
					return false;
				}
			}
		}
		catch
		{
		}
		return true;
	}

	private static void WriteMarker(RepairMarker marker)
	{
		try
		{
			Directory.CreateDirectory(Path.GetDirectoryName(MarkerPath));
			string text = $"{MarkerPath}.{Guid.NewGuid():N}.tmp";
			File.WriteAllText(text, JsonSerializer.Serialize(marker));
			File.Move(text, MarkerPath, overwrite: true);
		}
		catch
		{
		}
	}

	private static string? NullIfEmpty(string? value)
	{
		if (!string.IsNullOrWhiteSpace(value))
		{
			return value;
		}
		return null;
	}
}
