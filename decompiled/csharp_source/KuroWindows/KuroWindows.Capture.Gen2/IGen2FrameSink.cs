using System;
using KuroWindows.RecorderCore.Clock;
using Vortice.Direct3D11;

namespace KuroWindows.Capture.Gen2;

internal interface IGen2FrameSink
{
	bool ShouldDeliverFrame(long sourceQpc, long captureQpc, bool bypassPacing = false);

	void DeliverFrame(ID3D11Texture2D ownerDeviceTexture, long sourceQpc, long captureQpc, TimestampKind timestampKind, TimestampQuality timestampQuality);

	void DeliverCursorOnlyFrame(long sourceQpc);

	void SourceFailed(string stage, Exception ex);

	void SourceAccessDenied();

	void RecordAcquireOutcome(Gen2AcquireOutcome outcome, long accumulatedFrames);
}
