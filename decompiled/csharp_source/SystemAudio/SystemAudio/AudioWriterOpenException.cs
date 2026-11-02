using System;

namespace SystemAudio;

internal sealed class AudioWriterOpenException(string stage, string hr, Exception? inner = null) : Exception("system audio writer open failed at " + stage + " hr=" + hr, inner)
{
	public string Stage { get; } = stage;

	public string Hr { get; } = hr;

	public string Reason => "encoder_open_failed:" + Stage + ":" + Hr;
}
