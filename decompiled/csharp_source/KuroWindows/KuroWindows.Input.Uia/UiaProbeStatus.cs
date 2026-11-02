namespace KuroWindows.Input.Uia;

internal enum UiaProbeStatus : byte
{
	Ok,
	NoFocusedElement,
	ProviderError,
	TargetChanged,
	Unavailable
}
