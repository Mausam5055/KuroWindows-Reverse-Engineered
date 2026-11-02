using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ClientTelemetry;

namespace KuroWindows.Net;

internal sealed class DnsFallbackResolver
{
	private sealed class HostDnsHealth
	{
		public bool Degraded;

		public long? LastDegradedLineAtMs;

		public long SuppressedRescues;

		public long? LastRecoveredLineAtMs;

		public long SuppressedRecoveries;

		public long? LastExhaustedLineAtMs;

		public long SuppressedFailures;
	}

	internal static readonly TimeSpan DefaultFallbackConnectTimeout = TimeSpan.FromSeconds(5.0);

	internal static readonly TimeSpan DefaultDohQueryTimeout = TimeSpan.FromSeconds(3.0);

	internal static readonly TimeSpan RemoteDnsLineMinInterval = TimeSpan.FromMinutes(10.0);

	internal const int MaxTrackedTelemetryHosts = 64;

	internal static readonly DohResolverEndpoint[] DohResolvers = new DohResolverEndpoint[2]
	{
		new DohResolverEndpoint("1.1.1.1", "https://1.1.1.1/dns-query?name={0}&type=A"),
		new DohResolverEndpoint("8.8.8.8", "https://8.8.8.8/resolve?name={0}&type=A")
	};

	private readonly DnsLastGoodCache _cache;

	private readonly Func<string, bool> _isPlatformHost;

	private readonly Func<string, CancellationToken, ValueTask<IPAddress[]>> _resolveHost;

	private readonly Func<IPAddress[], int, CancellationToken, ValueTask<Stream>> _connect;

	private readonly Func<string, int, CancellationToken, ValueTask<Stream>> _connectUnresolved;

	private readonly Func<Uri, CancellationToken, ValueTask<string>> _dohFetch;

	private readonly TimeSpan _fallbackConnectTimeout;

	private readonly TimeSpan _dohQueryTimeout;

	private readonly object _healthLock = new object();

	private readonly Dictionary<string, HostDnsHealth> _hostHealth = new Dictionary<string, HostDnsHealth>(StringComparer.OrdinalIgnoreCase);

	internal DnsFallbackResolver(DnsLastGoodCache cache, Func<string, bool> isPlatformHost, Func<string, CancellationToken, ValueTask<IPAddress[]>> resolveHost, Func<IPAddress[], int, CancellationToken, ValueTask<Stream>> connect, Func<string, int, CancellationToken, ValueTask<Stream>> connectUnresolved, Func<Uri, CancellationToken, ValueTask<string>> dohFetch, TimeSpan? fallbackConnectTimeout = null, TimeSpan? dohQueryTimeout = null)
	{
		_cache = cache;
		_isPlatformHost = isPlatformHost;
		_resolveHost = resolveHost;
		_connect = connect;
		_connectUnresolved = connectUnresolved;
		_dohFetch = dohFetch;
		_fallbackConnectTimeout = fallbackConnectTimeout ?? DefaultFallbackConnectTimeout;
		_dohQueryTimeout = dohQueryTimeout ?? DefaultDohQueryTimeout;
	}

	public async ValueTask<Stream> GetConnectionAsync(string host, int port, CancellationToken ct)
	{
		if (IPAddress.TryParse(host, out IPAddress _) || !_isPlatformHost(host))
		{
			return await _connectUnresolved(host, port, ct).ConfigureAwait(continueOnCapturedContext: false);
		}
		ExceptionDispatchInfo resolutionFailure = null;
		IPAddress[] addresses = Array.Empty<IPAddress>();
		try
		{
			addresses = await _resolveHost(host, ct).ConfigureAwait(continueOnCapturedContext: false);
			if (addresses == null || addresses.Length <= 0)
			{
				resolutionFailure = ExceptionDispatchInfo.Capture(new SocketException(11001));
			}
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception source)
		{
			resolutionFailure = ExceptionDispatchInfo.Capture(source);
		}
		if (resolutionFailure == null)
		{
			string text = NoteRecovered(host, Environment.TickCount64);
			if (text != null)
			{
				KuroLog.App(text, "GetConnectionAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Net\\ResilientDns.cs", 383);
				Count.DnsFallback(DnsEvent.Recovered, 1L);
			}
			Stream result = await _connect(addresses, port, ct).ConfigureAwait(continueOnCapturedContext: false);
			_cache.Refresh(host, addresses);
			return result;
		}
		IReadOnlyList<IPAddress> cached = _cache.Get(host);
		foreach (IPAddress ip in cached.Take(3))
		{
			Stream stream = await TryConnectBoundedAsync(ip, port, ct).ConfigureAwait(continueOnCapturedContext: false);
			if (stream != null)
			{
				_cache.Promote(host, ip);
				KuroLog.LocalOnly("app", $"dns fallback connected host={host} path=cache ip={ip}", "GetConnectionAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Net\\ResilientDns.cs", 409);
				string text2 = NoteRescue(host, "cache", resolutionFailure.SourceException.GetType().Name, Environment.TickCount64);
				if (text2 != null)
				{
					KuroLog.App(text2, "GetConnectionAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Net\\ResilientDns.cs", 414);
					Count.DnsFallback(DnsEvent.Degraded, 1L);
				}
				return stream;
			}
		}
		DohResolverEndpoint[] dohResolvers = DohResolvers;
		foreach (DohResolverEndpoint resolver in dohResolvers)
		{
			List<IPAddress> answers;
			try
			{
				using CancellationTokenSource dohCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
				dohCts.CancelAfter(_dohQueryTimeout);
				answers = ParseDohAnswers(await _dohFetch(resolver.QueryUri(host), dohCts.Token).ConfigureAwait(continueOnCapturedContext: false));
			}
			catch (OperationCanceledException) when (ct.IsCancellationRequested)
			{
				throw;
			}
			catch
			{
				continue;
			}
			foreach (IPAddress ip in answers)
			{
				Stream stream2 = await TryConnectBoundedAsync(ip, port, ct).ConfigureAwait(continueOnCapturedContext: false);
				if (stream2 != null)
				{
					_cache.Refresh(host, answers, ip);
					KuroLog.LocalOnly("app", $"dns fallback connected host={host} path=doh resolver={resolver.Name} ip={ip}", "GetConnectionAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Net\\ResilientDns.cs", 448);
					string text3 = NoteRescue(host, "doh", resolutionFailure.SourceException.GetType().Name, Environment.TickCount64);
					if (text3 != null)
					{
						KuroLog.App(text3, "GetConnectionAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Net\\ResilientDns.cs", 455);
						Count.DnsFallback(DnsEvent.Degraded, 1L);
					}
					return stream2;
				}
			}
			answers = null;
		}
		Exception sourceException = resolutionFailure.SourceException;
		KuroLog.LocalOnly("app", $"dns fallback exhausted host={host} cached_ips={cached.Count} error={sourceException.GetType().Name}: {sourceException.Message}", "GetConnectionAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Net\\ResilientDns.cs", 473);
		string text4 = NoteExhausted(host, cached.Count, sourceException.GetType().Name, Environment.TickCount64);
		if (text4 != null)
		{
			KuroLog.App(text4, "GetConnectionAsync", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Net\\ResilientDns.cs", 480);
			Count.DnsFallback(DnsEvent.Exhausted, 1L);
			Telemetry.Emit(Ev.DnsFallbackExhausted(TelemetryMap.EndpointFor(host), sourceException.GetType().Name + ": " + sourceException.Message));
		}
		resolutionFailure.Throw();
		throw sourceException;
	}

	private async ValueTask<Stream?> TryConnectBoundedAsync(IPAddress ip, int port, CancellationToken ct)
	{
		using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
		cts.CancelAfter(_fallbackConnectTimeout);
		try
		{
			return await _connect(new IPAddress[1] { ip }, port, cts.Token).ConfigureAwait(continueOnCapturedContext: false);
		}
		catch (OperationCanceledException) when (ct.IsCancellationRequested)
		{
			throw;
		}
		catch
		{
			return null;
		}
	}

	internal string? NoteRescue(string host, string path, string errorType, long nowMs)
	{
		lock (_healthLock)
		{
			HostDnsHealth orAddHealthLocked = GetOrAddHealthLocked(host);
			if (orAddHealthLocked == null)
			{
				return null;
			}
			orAddHealthLocked.Degraded = true;
			long? lastDegradedLineAtMs = orAddHealthLocked.LastDegradedLineAtMs;
			if (lastDegradedLineAtMs.HasValue)
			{
				long valueOrDefault = lastDegradedLineAtMs.GetValueOrDefault();
				if (nowMs - valueOrDefault < (long)RemoteDnsLineMinInterval.TotalMilliseconds)
				{
					orAddHealthLocked.SuppressedRescues++;
					return null;
				}
			}
			string result = $"dns fallback degraded host={host} path={path} error={errorType} suppressed_rescues={orAddHealthLocked.SuppressedRescues}";
			orAddHealthLocked.LastDegradedLineAtMs = nowMs;
			orAddHealthLocked.SuppressedRescues = 0L;
			return result;
		}
	}

	internal string? NoteRecovered(string host, long nowMs)
	{
		lock (_healthLock)
		{
			if (!_hostHealth.TryGetValue(host, out HostDnsHealth value) || !value.Degraded)
			{
				return null;
			}
			value.Degraded = false;
			long? lastRecoveredLineAtMs = value.LastRecoveredLineAtMs;
			if (lastRecoveredLineAtMs.HasValue)
			{
				long valueOrDefault = lastRecoveredLineAtMs.GetValueOrDefault();
				if (nowMs - valueOrDefault < (long)RemoteDnsLineMinInterval.TotalMilliseconds)
				{
					value.SuppressedRecoveries++;
					return null;
				}
			}
			string result = $"dns fallback recovered host={host} suppressed_recoveries={value.SuppressedRecoveries}";
			value.LastRecoveredLineAtMs = nowMs;
			value.SuppressedRecoveries = 0L;
			return result;
		}
	}

	internal string? NoteExhausted(string host, int cachedIps, string errorType, long nowMs)
	{
		lock (_healthLock)
		{
			HostDnsHealth orAddHealthLocked = GetOrAddHealthLocked(host);
			if (orAddHealthLocked == null)
			{
				return null;
			}
			orAddHealthLocked.Degraded = true;
			long? lastExhaustedLineAtMs = orAddHealthLocked.LastExhaustedLineAtMs;
			if (lastExhaustedLineAtMs.HasValue)
			{
				long valueOrDefault = lastExhaustedLineAtMs.GetValueOrDefault();
				if (nowMs - valueOrDefault < (long)RemoteDnsLineMinInterval.TotalMilliseconds)
				{
					orAddHealthLocked.SuppressedFailures++;
					return null;
				}
			}
			string result = $"dns fallback exhausted host={host} cached_ips={cachedIps} error={errorType} suppressed_failures={orAddHealthLocked.SuppressedFailures}";
			orAddHealthLocked.LastExhaustedLineAtMs = nowMs;
			orAddHealthLocked.SuppressedFailures = 0L;
			return result;
		}
	}

	private HostDnsHealth? GetOrAddHealthLocked(string host)
	{
		if (_hostHealth.TryGetValue(host, out HostDnsHealth value))
		{
			return value;
		}
		if (_hostHealth.Count >= 64)
		{
			return null;
		}
		value = new HostDnsHealth();
		_hostHealth[host] = value;
		return value;
	}

	internal static List<IPAddress> ParseDohAnswers(string json)
	{
		List<IPAddress> list = new List<IPAddress>();
		try
		{
			using JsonDocument jsonDocument = JsonDocument.Parse(json);
			JsonElement rootElement = jsonDocument.RootElement;
			if (rootElement.ValueKind != JsonValueKind.Object || !rootElement.TryGetProperty("Status", out var value) || value.ValueKind != JsonValueKind.Number || value.GetInt32() != 0 || !rootElement.TryGetProperty("Answer", out var value2) || value2.ValueKind != JsonValueKind.Array)
			{
				return list;
			}
			foreach (JsonElement item in value2.EnumerateArray())
			{
				if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("type", out var value3) && value3.ValueKind == JsonValueKind.Number && value3.GetInt32() == 1 && item.TryGetProperty("data", out var value4) && value4.ValueKind == JsonValueKind.String && IPAddress.TryParse(value4.GetString(), out IPAddress address) && address.AddressFamily == AddressFamily.InterNetwork && !list.Contains(address))
				{
					list.Add(address);
				}
			}
		}
		catch (JsonException)
		{
		}
		return list;
	}
}
