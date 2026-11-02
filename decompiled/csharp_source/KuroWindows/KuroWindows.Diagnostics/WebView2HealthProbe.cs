namespace KuroWindows.Diagnostics;

internal sealed record WebView2HealthProbe(string? LoaderVersion, string? RegisteredVersion, string? RegisteredLocation, bool BrowserExecutableExists, string? FolderOverride)
{
	public bool IsHealthy
	{
		get
		{
			if (!string.IsNullOrEmpty(LoaderVersion))
			{
				if (RegisteredVersion != null && RegisteredLocation != null)
				{
					return BrowserExecutableExists;
				}
				return true;
			}
			return false;
		}
	}
}
