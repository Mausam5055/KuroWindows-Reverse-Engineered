using System;
using KuroWindows.RecorderCore.Clock;
using KuroWindows.RecorderCore.Encode;

namespace KuroWindows.Capture;

internal interface ISegmentVideoEncoder : IDisposable
{
	Exception? FinalizeError { get; }

	uint TargetBitrate { get; }

	string EncoderBackend { get; }

	string InputMemoryPath { get; }

	bool HardwareTransformsRequested { get; }

	string EncoderSelectionReason { get; }

	string WireProfile { get; }

	EncodedFrameCommit WriteFrame(in FrameCommitRequest frame, long presentationTimeHns, long durationHns, long committedWallMs, TimestampQuality timestampQuality);
}
