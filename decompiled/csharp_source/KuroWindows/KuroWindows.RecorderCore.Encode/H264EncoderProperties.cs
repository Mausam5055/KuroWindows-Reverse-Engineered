namespace KuroWindows.RecorderCore.Encode;

internal readonly record struct H264EncoderProperties(int? RcModeHr, int? MeanBitRateHr, int? BFramesHr, int? CabacHr, string? Error)
{
	public bool RateControlOk
	{
		get
		{
			int? rcModeHr = RcModeHr;
			if (rcModeHr.HasValue && rcModeHr.GetValueOrDefault() >= 0)
			{
				rcModeHr = MeanBitRateHr;
				if (rcModeHr.HasValue)
				{
					return rcModeHr.GetValueOrDefault() >= 0;
				}
				return false;
			}
			return false;
		}
	}

	public bool BFramesBanConfirmed
	{
		get
		{
			int? bFramesHr = BFramesHr;
			if (bFramesHr.HasValue)
			{
				return bFramesHr.GetValueOrDefault() >= 0;
			}
			return false;
		}
	}
}
