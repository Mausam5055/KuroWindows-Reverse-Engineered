using System;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Automation;
using System.Windows.Automation.Text;

namespace KuroWindows.Input.Uia;

internal static class UiaProbeWorker
{
	internal const string Argument = "--uia-probe";

	private const int ConnectTimeoutMs = 15000;

	private const uint SemFailCriticalErrors = 1u;

	private const uint SemNoGpFaultErrorBox = 2u;

	[DllImport("kernel32.dll")]
	private static extern uint SetErrorMode(uint mode);

	[DllImport("user32.dll")]
	private static extern nint GetForegroundWindow();

	internal static bool IsProbeLaunch(string[] args)
	{
		if (args.Length >= 2)
		{
			return string.Equals(args[0], "--uia-probe", StringComparison.Ordinal);
		}
		return false;
	}

	internal static void Run(string[] args)
	{
		string pipeName = args[1];
		int parentPid = ((args.Length >= 3 && int.TryParse(args[2], out var result)) ? result : 0);
		SuppressCrashUi();
		StartParentReaper(parentPid);
		Thread thread = new Thread(() =>
		{
			Serve(pipeName);
		});
		thread.IsBackground = false;
		thread.Name = "KuroUiaProbe";
		thread.SetApartmentState(ApartmentState.MTA);
		thread.Start();
		thread.Join();
	}

	private static void SuppressCrashUi()
	{
		try
		{
			SetErrorMode(3u);
		}
		catch
		{
		}
	}

	private static void StartParentReaper(int parentPid)
	{
		Thread thread = new Thread(() =>
		{
			try
			{
				if (parentPid > 0)
				{
					using Process process = Process.GetProcessById(parentPid);
					process.WaitForExit();
				}
			}
			catch
			{
			}
			try
			{
				Process.GetCurrentProcess().Kill();
			}
			catch
			{
			}
		});
		thread.IsBackground = true;
		thread.Name = "KuroUiaProbeReaper";
		thread.Start();
	}

	private static void Serve(string pipeName)
	{
		long num = 0L;
		try
		{
			using NamedPipeClientStream namedPipeClientStream = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut);
			namedPipeClientStream.Connect(15000);
			KuroLog.LocalOnly("uia-probe", $"uia probe helper connected pipe={pipeName} pid={Environment.ProcessId}", "Serve", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Input\\Uia\\UiaProbeWorker.cs", 146);
			while (true)
			{
				byte[] array = UiaProbeProtocol.ReadFrame(namedPipeClientStream);
				if (array == null)
				{
					break;
				}
				if (!UiaProbeProtocol.TryDecodeRequest(array, out var request))
				{
					KuroLog.LocalOnly("uia-probe", "uia probe helper received an undecodable frame; exiting", "Serve", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Input\\Uia\\UiaProbeWorker.cs", 159);
					break;
				}
				UiaProbeProtocol.WriteFrame(namedPipeClientStream, UiaProbeProtocol.EncodeResponse(Execute(in request)));
				num++;
			}
			KuroLog.LocalOnly("uia-probe", $"uia probe helper channel ended served={num}", "Serve", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Input\\Uia\\UiaProbeWorker.cs", 168);
		}
		catch (Exception ex)
		{
			KuroLog.LocalOnly("uia-probe", $"uia probe helper stopped served={num}: {ex.GetType().Name}: {ex.Message}", "Serve", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Input\\Uia\\UiaProbeWorker.cs", 174);
		}
	}

	private static UiaProbeResponse Execute(in UiaProbeRequest request)
	{
		if (request.TargetWindow != 0L && ((IntPtr)GetForegroundWindow()).ToInt64() != request.TargetWindow)
		{
			return new UiaProbeResponse(request.Seq, UiaProbeStatus.TargetChanged, IsSecure: false, null);
		}
		try
		{
			AutomationElement focusedElement = AutomationElement.FocusedElement;
			if (focusedElement == null)
			{
				return new UiaProbeResponse(request.Seq, UiaProbeStatus.NoFocusedElement, IsSecure: false, null);
			}
			bool isSecure = false;
			if ((request.Reads & UiaProbeReads.SecureField) != 0)
			{
				isSecure = IsSecureElement(focusedElement);
			}
			string selectedText = null;
			if ((request.Reads & UiaProbeReads.TextSelection) != 0)
			{
				selectedText = ReadSelectedText(focusedElement);
			}
			return new UiaProbeResponse(request.Seq, UiaProbeStatus.Ok, isSecure, selectedText);
		}
		catch
		{
			return new UiaProbeResponse(request.Seq, UiaProbeStatus.ProviderError, IsSecure: false, null);
		}
	}

	private static bool IsSecureElement(AutomationElement element)
	{
		try
		{
			object currentPropertyValue = element.GetCurrentPropertyValue(AutomationElement.IsPasswordProperty, ignoreDefaultValue: true);
			bool flag = default;
			int num;
			if (currentPropertyValue is bool)
			{
				flag = (bool)currentPropertyValue;
				num = 1;
			}
			else
			{
				num = 0;
			}
			return (byte)((uint)num & (flag ? 1u : 0u)) != 0;
		}
		catch
		{
			return false;
		}
	}

	private static string? ReadSelectedText(AutomationElement element)
	{
		if (!element.TryGetCurrentPattern(TextPattern.Pattern, out var patternObject))
		{
			return null;
		}
		TextPattern textPattern = (TextPattern)patternObject;
		if (textPattern.SupportedTextSelection == SupportedTextSelection.None)
		{
			return null;
		}
		TextPatternRange[] selection = textPattern.GetSelection();
		if (selection.Length == 0)
		{
			return null;
		}
		string text = selection[0].GetText(500);
		if (!string.IsNullOrEmpty(text))
		{
			return text;
		}
		return null;
	}
}
