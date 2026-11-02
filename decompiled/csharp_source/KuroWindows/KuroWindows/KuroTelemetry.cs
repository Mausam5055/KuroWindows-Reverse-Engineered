using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using ClientTelemetry;
using KuroWindows.Auth;
using KuroWindows.Net;
using KuroWindows.Sync;

namespace KuroWindows;

internal static class KuroTelemetry
{
	private static readonly string StateDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppConfig.LocalDataFolderName, "telemetry");

	public static void Start(bool sendEnabled)
	{
		Version version = Environment.OSVersion.Version;
		ResilientDns.RegisterEndpointHost(AppConfig.TelemetryIngestUrl.Host);
		Telemetry.Start(new TelemetryHost("kuro", AppConfig.AppVersion, "windows", StateDirectory, AppConfig.TelemetryIngestUrl, (CancellationToken ct) => new ValueTask<string>(TokenAsync(ct)))
		{
			OsVersion = version.ToString(3),
			OsBuild = version.Build.ToString(),
			Arch = RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(),
			LocalLog = (string line) =>
			{
				KuroLog.LocalOnly("telemetry", line, "Start", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Telemetry\\KuroTelemetry.cs", 42);
			},
			HttpHandler = ResilientDns.CreateHandler(),
			SendEnabled = sendEnabled
		});
	}

	public static void ApplyConfig(DesktopConfig config)
	{
		Telemetry.ApplyConfig(config.Telemetry, config.LoggingEnabled);
	}

	public static void SyncIdentity()
	{
		Telemetry.SetIdentity(AuthManager.Instance.Session?.UserId, AuthManager.Instance.DeviceId);
	}

	public static void OnAuthStateChanged(KuroWindows.Auth.AuthState state)
	{
		SyncIdentity();
		Telemetry.Emit(Ev.AuthStateChanged(TelemetryMap.AuthStateFor(state)));
	}

	private static async Task<string?> TokenAsync(CancellationToken ct)
	{
		AuthManager instance = AuthManager.Instance;
		if (!instance.IsSignedIn)
		{
			return null;
		}
		try
		{
			return await instance.GetValidTokenAsync(ct).ConfigureAwait(continueOnCapturedContext: false);
		}
		catch (Exception)
		{
			return null;
		}
	}
}
