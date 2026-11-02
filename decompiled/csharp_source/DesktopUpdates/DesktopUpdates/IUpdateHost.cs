using System;
using System.Threading;
using System.Threading.Tasks;

namespace DesktopUpdates;

public interface IUpdateHost
{
	bool IsShuttingDown { get; }

	bool SignInInFlight { get; }

	bool WindowInFront { get; }

	Task EnterBelowFloorAsync();

	void ExitBelowFloor();

	Task<bool> EndRecordingForInstallAsync(bool immediate, CancellationToken ct);

	void ResumeRecordingAfterAbortedInstall();

	Task DrainUploadsAsync(TimeSpan budget);

	void MarkCleanExitForInstall(string exitReason);

	void MarkExitAborted(string source, Exception cause);

	void StopHelperProcessesForInstall(string reason);

	void CloseWindowForInstall();
}
