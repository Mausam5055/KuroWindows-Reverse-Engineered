using System;

namespace SystemAudio;

internal sealed record EndpointOpenFailure(string Reason, int Hr, SystemAudioMixFormat? MixFormat = null)
{
	public string SegmentReason
	{
		get
		{
			if (!Reason.StartsWith("capture_format:", StringComparison.Ordinal))
			{
				return "no_endpoint";
			}
			return Reason;
		}
	}
}
