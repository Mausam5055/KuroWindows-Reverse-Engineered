namespace KuroWindows.Capture;

internal delegate ISegmentVideoEncoder SegmentEncoderFactory(string outputPath, int width, int height, int captureFps);
