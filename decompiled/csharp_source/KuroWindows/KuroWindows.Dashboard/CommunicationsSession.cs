using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace KuroWindows.Dashboard;

public sealed class CommunicationsSession : IDisposable
{
	private static readonly JsonSerializerOptions EnvelopeJsonOptions = new JsonSerializerOptions
	{
		Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
	};

	private readonly object _gate = new object();

	private readonly Func<string?> _currentOwner;

	private readonly Func<string?, CancellationToken, Task<JsonElement>> _fetch;

	private readonly Func<DateTimeOffset> _now;

	private readonly Func<Exception, bool> _isUnsupportedError;

	private string? _owner;

	private int _generation;

	private JsonElement? _cache;

	private string? _revision;

	private string? _desiredRevision;

	private DateTimeOffset? _nextTransitionAt;

	private DateTimeOffset? _fetchedAt;

	private DateTimeOffset? _attemptedAt;

	private Task<JsonElement>? _inFlight;

	private CancellationTokenSource _sessionCancellation = new CancellationTokenSource();

	private bool _disposed;

	private bool _unsupported;

	public int SessionGeneration
	{
		get
		{
			lock (_gate)
			{
				SynchronizeOwnerLocked();
				return _generation;
			}
		}
	}

	public bool HasEnvelope
	{
		get
		{
			lock (_gate)
			{
				SynchronizeOwnerLocked();
				return _cache.HasValue && !_unsupported;
			}
		}
	}

	public event Action<JsonElement>? Changed;

	public event Action<JsonElement>? Unsupported;

	public CommunicationsSession(Func<string?> currentOwner, Func<string?, CancellationToken, Task<JsonElement>> fetch, Func<DateTimeOffset>? now = null, Func<Exception, bool>? isUnsupportedError = null)
	{
		_currentOwner = currentOwner;
		_fetch = fetch;
		_now = now ?? ((Func<DateTimeOffset>)(() => DateTimeOffset.UtcNow));
		_isUnsupportedError = isUnsupportedError ?? ((Func<Exception, bool>)((Exception _) => false));
	}

	public void SynchronizeOwner()
	{
		lock (_gate)
		{
			SynchronizeOwnerLocked();
		}
	}

	private void SynchronizeOwnerLocked()
	{
		string text = _currentOwner()?.ToLowerInvariant();
		if (!(text == _owner))
		{
			ClearLocked();
			_owner = text;
		}
	}

	public void Reset()
	{
		lock (_gate)
		{
			ClearLocked();
		}
	}

	private void ClearLocked()
	{
		_generation++;
		_sessionCancellation.Cancel();
		_sessionCancellation.Dispose();
		_sessionCancellation = new CancellationTokenSource();
		_inFlight = null;
		_cache = null;
		_revision = null;
		_desiredRevision = null;
		_nextTransitionAt = null;
		_fetchedAt = null;
		_attemptedAt = null;
		_owner = null;
		_unsupported = false;
	}

	public void Observe(JsonElement hint)
	{
		if (!TryReadHint(hint, out string revision, out DateTimeOffset? transition))
		{
			return;
		}
		lock (_gate)
		{
			if (_disposed)
			{
				return;
			}
			SynchronizeOwnerLocked();
			if (_owner == null)
			{
				return;
			}
			_desiredRevision = revision;
			_nextTransitionAt = transition;
			bool flag = _cache.HasValue && (_cache.Value.GetProperty("guard_notice").ValueKind == JsonValueKind.Object || (hint.TryGetProperty("revalidate_on_sync", out var value) && value.ValueKind == JsonValueKind.True)) && (!_fetchedAt.HasValue || _now() - _fetchedAt.Value >= TimeSpan.FromSeconds(60.0));
			if (_cache.HasValue && !_unsupported && revision == _revision && !flag && !(transition <= _now()))
			{
				return;
			}
		}
		RefreshQuietlyAsync();
	}

	private async Task RefreshQuietlyAsync()
	{
		try
		{
			await RefreshAsync().ConfigureAwait(continueOnCapturedContext: false);
		}
		catch
		{
		}
	}

	public Task<JsonElement> RefreshAsync(bool force = false, CancellationToken cancellationToken = default(CancellationToken))
	{
		Task<JsonElement> task;
		lock (_gate)
		{
			ObjectDisposedException.ThrowIf(_disposed, this);
			SynchronizeOwnerLocked();
			if (_owner == null)
			{
				throw new OperationCanceledException();
			}
			if (_inFlight != null)
			{
				task = _inFlight;
			}
			else
			{
				DateTimeOffset dateTimeOffset = _now();
				bool flag = _desiredRevision != null && _desiredRevision != _revision;
				bool flag2 = _nextTransitionAt <= dateTimeOffset;
				bool flag3 = _fetchedAt.HasValue && dateTimeOffset - _fetchedAt.Value < TimeSpan.FromSeconds(60.0);
				bool flag4 = _attemptedAt.HasValue && dateTimeOffset - _attemptedAt.Value < TimeSpan.FromSeconds(1.0);
				if (_cache.HasValue && !_unsupported && (flag4 || ((!force & flag3) && !flag && !flag2)))
				{
					task = Task.FromResult(_cache.Value);
				}
				else
				{
					if (flag4)
					{
						throw new InvalidOperationException("Communications unavailable.");
					}
					_attemptedAt = dateTimeOffset;
					task = (_inFlight = FetchAsync(_owner, _generation, _revision, _sessionCancellation.Token));
				}
			}
		}
		if (!cancellationToken.CanBeCanceled)
		{
			return task;
		}
		return task.WaitAsync(cancellationToken);
	}

	private async Task<JsonElement> FetchAsync(string owner, int generation, string? revision, CancellationToken cancellationToken)
	{
		await Task.Yield();
		try
		{
			JsonElement jsonElement = await _fetch(revision, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
			cancellationToken.ThrowIfCancellationRequested();
			JsonObject jsonObject = JsonNode.Parse(jsonElement.GetRawText()).AsObject();
			if (jsonElement.TryGetProperty("owner_user_id", out var value) && value.ValueKind == JsonValueKind.String)
			{
				jsonObject["owner_user_id"] = value.GetString().ToLowerInvariant();
			}
			jsonObject["session_generation"] = generation;
			jsonElement = JsonSerializer.SerializeToElement(jsonObject, EnvelopeJsonOptions);
			Validate(jsonElement, owner);
			bool flag;
			lock (_gate)
			{
				EnsureSessionLocked(owner, generation);
				flag = _unsupported || !_cache.HasValue || _cache.Value.GetRawText() != jsonElement.GetRawText();
				_unsupported = false;
				_cache = jsonElement.Clone();
				_revision = jsonElement.GetProperty("revision").GetString();
				_nextTransitionAt = ReadTransition(jsonElement);
				_fetchedAt = _now();
				_inFlight = null;
			}
			if (flag)
			{
				Changed?.Invoke(jsonElement);
			}
			return jsonElement;
		}
		catch (Exception arg)
		{
			JsonElement? jsonElement2 = null;
			lock (_gate)
			{
				EnsureSessionLocked(owner, generation);
				_inFlight = null;
				if (_isUnsupportedError(arg))
				{
					if (!_unsupported)
					{
						jsonElement2 = JsonSerializer.SerializeToElement(new
						{
							owner_user_id = owner,
							session_generation = generation
						});
					}
					_unsupported = true;
				}
				else if (_cache.HasValue && !_unsupported)
				{
					return _cache.Value;
				}
			}
			if (jsonElement2.HasValue)
			{
				Unsupported?.Invoke(jsonElement2.Value);
			}
			throw;
		}
	}

	private void EnsureSessionLocked(string owner, int generation)
	{
		if (_disposed || _generation != generation || _currentOwner()?.ToLowerInvariant() != owner)
		{
			throw new OperationCanceledException();
		}
	}

	public static bool TryReadHint(JsonElement hint, out string? revision, out DateTimeOffset? transition)
	{
		revision = null;
		transition = null;
		if (hint.ValueKind != JsonValueKind.Object || !hint.TryGetProperty("schema_version", out var value) || value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var value2) || value2 != 1 || !hint.TryGetProperty("revision", out var value3) || value3.ValueKind != JsonValueKind.String)
		{
			return false;
		}
		revision = value3.GetString();
		if (string.IsNullOrWhiteSpace(revision) || revision.Length > 160)
		{
			return false;
		}
		if (hint.TryGetProperty("next_transition_at", out var value4) && value4.ValueKind != JsonValueKind.Null && !Text(hint, "next_transition_at", 80))
		{
			return false;
		}
		bool flag = hint.TryGetProperty("revalidate_on_sync", out var value5);
		if (flag)
		{
			JsonValueKind valueKind = value5.ValueKind;
			bool flag2 = valueKind - 5 <= JsonValueKind.Array;
			flag = !flag2;
		}
		if (flag)
		{
			return false;
		}
		transition = ReadTransition(hint);
		return true;
	}

	private static DateTimeOffset? ReadTransition(JsonElement payload)
	{
		if (!payload.TryGetProperty("next_transition_at", out var value) || value.ValueKind != JsonValueKind.String || !value.TryGetDateTimeOffset(out var value2))
		{
			return null;
		}
		return value2;
	}

	private static void Validate(JsonElement payload, string owner)
	{
		JsonElement value2 = default;
		bool flag = !TryReadHint(payload, out string _, out DateTimeOffset? _) || !payload.TryGetProperty("owner_user_id", out var value) || value.ValueKind != JsonValueKind.String || !string.Equals(value.GetString(), owner, StringComparison.OrdinalIgnoreCase) || !payload.TryGetProperty("signups_enabled", out value2);
		if (!flag)
		{
			JsonValueKind valueKind = value2.ValueKind;
			bool flag2 = valueKind - 5 <= JsonValueKind.Object;
			flag = !flag2;
		}
		JsonElement value3 = default;
		JsonElement value4 = default;
		bool flag3 = flag || !payload.TryGetProperty("notices", out value3) || value3.ValueKind != JsonValueKind.Array || value3.GetArrayLength() > 32 || !payload.TryGetProperty("guard_notice", out value4);
		if (!flag3)
		{
			JsonValueKind valueKind = value4.ValueKind;
			bool flag2 = ((valueKind == JsonValueKind.Object || valueKind == JsonValueKind.Null) ? true : false);
			flag3 = !flag2;
		}
		if (flag3 || Encoding.UTF8.GetByteCount(payload.GetRawText()) > 32768)
		{
			throw new InvalidDataException("Invalid communications envelope.");
		}
		if (value3.EnumerateArray().Any((JsonElement notice) => notice.ValueKind != JsonValueKind.Object || !Text(notice, "id", 160) || !Text(notice, "title", 240) || !Text(notice, "subline", 2000, empty: true) || !notice.TryGetProperty("placement", out var value8) || value8.ValueKind != JsonValueKind.String || !Enumerable.Contains(new string[3] { "account", "earnings", "referrals" }, value8.GetString())))
		{
			throw new InvalidDataException("Invalid communications envelope.");
		}
		if (payload.TryGetProperty("next_transition_at", out var value5) && value5.ValueKind != JsonValueKind.Null && !Text(payload, "next_transition_at", 80))
		{
			throw new InvalidDataException("Invalid communications envelope.");
		}
		if (value4.ValueKind == JsonValueKind.Object && (!value4.TryGetProperty("id", out var value6) || value6.ValueKind != JsonValueKind.String || !Guid.TryParse(value6.GetString(), out var _) || !Text(value4, "revision", 160) || !Text(value4, "title", 240) || !Text(value4, "reason", 2000) || !Text(value4, "correction", 2000) || !Text(value4, "email_hint", 500) || (value4.TryGetProperty("effective_delivery_at", out var value7) && value7.ValueKind != JsonValueKind.Null && !Text(value4, "effective_delivery_at", 80))))
		{
			throw new InvalidDataException("Invalid communications envelope.");
		}
	}

	private static bool Text(JsonElement source, string field, int max, bool empty = false)
	{
		if (source.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String && value.GetString().Length <= max)
		{
			if (!empty)
			{
				return !string.IsNullOrWhiteSpace(value.GetString());
			}
			return true;
		}
		return false;
	}

	public void Dispose()
	{
		lock (_gate)
		{
			if (!_disposed)
			{
				_disposed = true;
				ClearLocked();
				_sessionCancellation.Dispose();
			}
		}
	}
}
