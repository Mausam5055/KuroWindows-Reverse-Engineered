using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Threading;
using ClientTelemetry;
using Microsoft.Win32;

namespace KuroWindows.Diagnostics;

internal static class CrashDiagnostics
{
	private static int _processHandlersInstalled;

	private static int _dispatcherHandlerInstalled;

	public static void InstallProcessHandlers()
	{
		if (Interlocked.Exchange(ref _processHandlersInstalled, 1) != 0)
		{
			return;
		}
		AppDomain.CurrentDomain.UnhandledException += (object _, UnhandledExceptionEventArgs ex) =>
		{
			if (ex.ExceptionObject is Exception ex2)
			{
				KuroRunStateTracker.MarkFatal("appdomain_unhandled_exception", ex2);
			}
			KuroLog.Crash($"UNHANDLED terminating={ex.IsTerminating}: {ex.ExceptionObject}", "InstallProcessHandlers", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Diagnostics\\CrashDiagnostics.cs", 27);
			Telemetry.FlushToDisk();
		};
		TaskScheduler.UnobservedTaskException += (object? _, UnobservedTaskExceptionEventArgs ex) =>
		{
			Contained(ContainedExceptionSource.UnobservedTask, "task_unobserved_exception", "UNOBSERVED_TASK", ex.Exception.Flatten().InnerExceptions.FirstOrDefault() ?? ex.Exception, ex.Exception);
			ex.SetObserved();
		};
		System.Windows.Forms.Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
		System.Windows.Forms.Application.ThreadException += (object _, ThreadExceptionEventArgs ex) =>
		{
			Contained(ContainedExceptionSource.Winforms, "winforms_thread_exception", "WINFORMS_THREAD", ex.Exception, ex.Exception);
		};
		AppDomain.CurrentDomain.ProcessExit += (object? _, EventArgs _) =>
		{
			KuroRunStateTracker.MarkMilestone("process_exit");
			Telemetry.FlushToDisk();
		};
	}

	private static void Contained(ContainedExceptionSource source, string runStateSource, string logToken, Exception recorded, Exception full)
	{
		KuroRunStateTracker.MarkError(runStateSource, recorded);
		KuroLog.Crash($"{logToken}: {full}", "Contained", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Diagnostics\\CrashDiagnostics.cs", 61);
		Count.ContainedException(source, 1L);
		Telemetry.FlushToDisk();
	}

	public static void ConfigureWerLocalDumps()
	{
		try
		{
			string text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppConfig.LocalDataFolderName, "dumps");
			Directory.CreateDirectory(text);
			bool flag = false;
			try
			{
				using RegistryKey registryKey = Registry.LocalMachine.CreateSubKey("Software\\Microsoft\\Windows\\Windows Error Reporting\\LocalDumps\\KuroWindows.exe");
				if (registryKey != null)
				{
					registryKey.SetValue("DumpFolder", text, RegistryValueKind.ExpandString);
					registryKey.SetValue("DumpType", 2, RegistryValueKind.DWord);
					registryKey.SetValue("DumpCount", 2, RegistryValueKind.DWord);
					flag = true;
				}
			}
			catch
			{
			}
			using RegistryKey registryKey2 = Registry.CurrentUser.CreateSubKey("Software\\Microsoft\\Windows\\Windows Error Reporting\\LocalDumps\\KuroWindows.exe");
			if (registryKey2 == null && !flag)
			{
				KuroLog.LocalOnly("crash", "WER LocalDumps setup skipped: registry key unavailable", "ConfigureWerLocalDumps", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Diagnostics\\CrashDiagnostics.cs", 106);
				return;
			}
			if (registryKey2 != null)
			{
				registryKey2.SetValue("DumpFolder", text, RegistryValueKind.ExpandString);
				registryKey2.SetValue("DumpType", 2, RegistryValueKind.DWord);
				registryKey2.SetValue("DumpCount", 2, RegistryValueKind.DWord);
			}
			KuroLog.LocalOnly("crash", $"WER LocalDumps configured folder={Path.GetFileName(text)} dump_type=full dump_count=2 hklm={flag}", "ConfigureWerLocalDumps", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Diagnostics\\CrashDiagnostics.cs", 116);
		}
		catch (Exception ex)
		{
			KuroLog.LocalOnly("crash", "WER LocalDumps setup failed: " + ex.Message, "ConfigureWerLocalDumps", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Diagnostics\\CrashDiagnostics.cs", 120);
		}
	}

	public static void InstallDispatcherHandler(System.Windows.Application app)
	{
		if (Interlocked.Exchange(ref _dispatcherHandlerInstalled, 1) == 0)
		{
			app.DispatcherUnhandledException += (object _, DispatcherUnhandledExceptionEventArgs ex) =>
			{
				Contained(ContainedExceptionSource.Dispatcher, "wpf_dispatcher_exception", "DISPATCHER", ex.Exception, ex.Exception);
				ex.Handled = true;
			};
		}
	}
}
