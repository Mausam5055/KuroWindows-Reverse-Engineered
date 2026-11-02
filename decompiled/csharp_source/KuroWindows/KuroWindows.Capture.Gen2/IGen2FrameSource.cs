using System;
using System.Threading.Tasks;
using KuroWindows.RecorderCore.Capture;

namespace KuroWindows.Capture.Gen2;

internal interface IGen2FrameSource : IDisposable
{
	string FrontEndName { get; }

	string RuntimeFailureKey { get; }

	CaptureBackendKind BackendKind { get; }

	CapturePathSelection CapturePath { get; }

	void Start();

	Task StopAsync();

	void RequestReseed();
}
