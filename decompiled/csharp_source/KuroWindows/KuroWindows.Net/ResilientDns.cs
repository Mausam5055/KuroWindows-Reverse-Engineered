using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace KuroWindows.Net;

public static class ResilientDns
{
	private static readonly object InitLock = new object();

	private static DnsFallbackResolver? _resolver;

	private static readonly ConcurrentDictionary<string, byte> RegisteredHosts = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);

	private static string[]? _configuredSuffixes;

	private static readonly HttpClient DohHttp = new HttpClient(new SocketsHttpHandler
	{
		PooledConnectionLifetime = TimeSpan.FromMinutes(15.0)
	})
	{
		Timeout = Timeout.InfiniteTimeSpan
	};

	public static void Initialize()
	{
		EnsureResolver();
	}

	public static SocketsHttpHandler CreateHandler()
	{
		return new SocketsHttpHandler
		{
			PooledConnectionLifetime = TimeSpan.FromMinutes(15.0),
			ConnectCallback = (SocketsHttpConnectionContext context, CancellationToken ct) => EnsureResolver().GetConnectionAsync(context.DnsEndPoint.Host, context.DnsEndPoint.Port, ct)
		};
	}

	public static void RegisterPresignedAuthority(string url)
	{
		if (Uri.TryCreate(url, UriKind.Absolute, out Uri result))
		{
			RegisterEndpointHost(result.Host);
		}
	}

	public static void RegisterEndpointHost(string? host)
	{
		if (!string.IsNullOrWhiteSpace(host) && !IPAddress.TryParse(host, out IPAddress _))
		{
			RegisteredHosts.TryAdd(host, 0);
		}
	}

	internal static bool IsPlatformHost(string host)
	{
		string[] array = ConfiguredSuffixes();
		foreach (string suffix in array)
		{
			if (MatchesSuffix(host, suffix))
			{
				return true;
			}
		}
		foreach (string key in RegisteredHosts.Keys)
		{
			if (MatchesSuffix(host, key))
			{
				return true;
			}
		}
		return false;
	}

	internal static bool MatchesSuffix(string host, string suffix)
	{
		if (!host.Equals(suffix, StringComparison.OrdinalIgnoreCase))
		{
			if (host.Length > suffix.Length + 1 && host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
			{
				return host[host.Length - suffix.Length - 1] == '.';
			}
			return false;
		}
		return true;
	}

	private static string[] ConfiguredSuffixes()
	{
		string[] configuredSuffixes = _configuredSuffixes;
		if (configuredSuffixes != null)
		{
			return configuredSuffixes;
		}
		List<string> list = new List<string>(1);
		TryAddHost(list, AppConfig.SupabaseUrl);
		return _configuredSuffixes = list.ToArray();
	}

	private static void TryAddHost(List<string> list, string url)
	{
		if (Uri.TryCreate(url, UriKind.Absolute, out Uri result) && !string.IsNullOrEmpty(result.Host) && !list.Contains(result.Host, StringComparer.OrdinalIgnoreCase))
		{
			list.Add(result.Host);
		}
	}

	private static DnsFallbackResolver EnsureResolver()
	{
		DnsFallbackResolver resolver = _resolver;
		if (resolver != null)
		{
			return resolver;
		}
		lock (InitLock)
		{
			if (_resolver == null)
			{
				DnsLastGoodCache dnsLastGoodCache = new DnsLastGoodCache();
				dnsLastGoodCache.Load();
				_resolver = new DnsFallbackResolver(dnsLastGoodCache, IsPlatformHost, (string host, CancellationToken ct) => new ValueTask<IPAddress[]>(Dns.GetHostAddressesAsync(host, ct)), ConnectResolvedAsync, ConnectUnresolvedAsync, FetchDohAsync);
			}
			return _resolver;
		}
	}

	private static async ValueTask<Stream> ConnectResolvedAsync(IPAddress[] addresses, int port, CancellationToken ct)
	{
		Socket socket = new Socket(SocketType.Stream, ProtocolType.Tcp)
		{
			NoDelay = true
		};
		try
		{
			await socket.ConnectAsync(addresses, port, ct).ConfigureAwait(continueOnCapturedContext: false);
			return new NetworkStream(socket, ownsSocket: true);
		}
		catch
		{
			socket.Dispose();
			throw;
		}
	}

	private static async ValueTask<Stream> ConnectUnresolvedAsync(string host, int port, CancellationToken ct)
	{
		Socket socket = new Socket(SocketType.Stream, ProtocolType.Tcp)
		{
			NoDelay = true
		};
		try
		{
			await socket.ConnectAsync(new DnsEndPoint(host, port), ct).ConfigureAwait(continueOnCapturedContext: false);
			return new NetworkStream(socket, ownsSocket: true);
		}
		catch
		{
			socket.Dispose();
			throw;
		}
	}

	private static async ValueTask<string> FetchDohAsync(Uri requestUri, CancellationToken ct)
	{
		using HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Get, requestUri);
		req.Headers.Accept.ParseAdd("application/dns-json");
		using HttpResponseMessage resp = await DohHttp.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(continueOnCapturedContext: false);
		resp.EnsureSuccessStatusCode();
		return await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(continueOnCapturedContext: false);
	}
}
