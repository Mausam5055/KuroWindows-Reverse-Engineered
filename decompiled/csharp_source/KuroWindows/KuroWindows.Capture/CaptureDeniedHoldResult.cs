namespace KuroWindows.Capture;

internal readonly struct CaptureDeniedHoldResult
{
	public CaptureDeniedHoldOutcome Outcome { get; }

	public CaptureAccessDeniedException? EscalationException { get; }

	private CaptureDeniedHoldResult(CaptureDeniedHoldOutcome outcome, CaptureAccessDeniedException? escalationException)
	{
		Outcome = outcome;
		EscalationException = escalationException;
	}

	public static CaptureDeniedHoldResult Recovered()
	{
		return new CaptureDeniedHoldResult(CaptureDeniedHoldOutcome.Recovered, null);
	}

	public static CaptureDeniedHoldResult Escalated(CaptureAccessDeniedException exception)
	{
		return new CaptureDeniedHoldResult(CaptureDeniedHoldOutcome.Escalated, exception);
	}

	public static CaptureDeniedHoldResult Cancelled()
	{
		return new CaptureDeniedHoldResult(CaptureDeniedHoldOutcome.Cancelled, null);
	}
}
