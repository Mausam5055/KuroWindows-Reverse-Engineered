using System.Threading;

namespace SystemAudio;

internal sealed class CaptureCounters
{
	private long _starvedPeriods;

	private long _deviceReopens;

	public long StarvedPeriods => Interlocked.Read(in _starvedPeriods);

	public long DeviceReopens => Interlocked.Read(in _deviceReopens);

	public void CountStarved()
	{
		Interlocked.Increment(ref _starvedPeriods);
	}

	public void CountReopen()
	{
		Interlocked.Increment(ref _deviceReopens);
	}
}
