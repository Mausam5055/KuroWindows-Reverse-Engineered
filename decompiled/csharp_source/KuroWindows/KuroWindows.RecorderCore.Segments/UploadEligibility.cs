using ClientTelemetry;

namespace KuroWindows.RecorderCore.Segments;

internal readonly record struct UploadEligibility(UploadEligibilityKind Kind, QuarantineReason? Reason = null, string? Detail = null)
{
	public static readonly UploadEligibility Eligible = new UploadEligibility(UploadEligibilityKind.Eligible);

	public static readonly UploadEligibility Wait = new UploadEligibility(UploadEligibilityKind.Wait);

	public static readonly UploadEligibility Withheld = new UploadEligibility(UploadEligibilityKind.Withheld);

	public static UploadEligibility Quarantine(QuarantineReason reason, string? detail = null)
	{
		return new UploadEligibility(UploadEligibilityKind.Quarantine, reason, detail);
	}
}
