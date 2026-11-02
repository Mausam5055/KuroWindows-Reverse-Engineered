using KuroWindows.RecorderCore.Encode;
using Vortice.MediaFoundation;

namespace KuroWindows.Capture;

internal readonly record struct OpenedWriter(IMFSinkWriter Writer, int StreamIndex, string WireProfile, string WireProfileSource, H264EncoderChainInfo Chain);
