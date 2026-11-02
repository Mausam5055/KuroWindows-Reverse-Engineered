using System;
using System.Globalization;

namespace KuroWindows.Net;

internal sealed record DohResolverEndpoint(string Name, string QueryTemplate)
{
	public Uri QueryUri(string host)
	{
		return new Uri(string.Format(CultureInfo.InvariantCulture, QueryTemplate, Uri.EscapeDataString(host)));
	}
}
