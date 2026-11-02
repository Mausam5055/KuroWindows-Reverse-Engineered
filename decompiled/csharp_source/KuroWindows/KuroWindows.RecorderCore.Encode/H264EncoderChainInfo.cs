namespace KuroWindows.RecorderCore.Encode;

internal readonly record struct H264EncoderChainInfo(int TransformCount, string? EncoderMftName, bool IsHardwareMft, uint? NegotiatedProfile, uint? NegotiatedLevel);
