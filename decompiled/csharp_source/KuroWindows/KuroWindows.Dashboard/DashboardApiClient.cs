using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using KuroWindows.Auth;

namespace KuroWindows.Dashboard;

public sealed class DashboardApiClient
{
	private sealed record DashboardParams
	{
		[JsonPropertyName("include_communications")]
		public bool IncludeCommunications { get; init; }

		[JsonPropertyName("tz")]
		[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
		public string? Tz { get; init; }
	}

	private sealed record HistoryParams
	{
		[JsonPropertyName("limit")]
		public int Limit { get; init; }

		[JsonPropertyName("offset")]
		public int Offset { get; init; }

		[JsonPropertyName("tz")]
		[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
		public string? Tz { get; init; }
	}

	private sealed record PayoutParams
	{
		[JsonPropertyName("method")]
		public string Method { get; init; } = "";

		[JsonPropertyName("destination")]
		[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
		public string? Destination { get; init; }
	}

	private const int DaysPageSize = 7;

	private const int TransactionsPageSize = 15;

	private static readonly object EmptyParams = new object();

	private readonly AuthManager _auth;

	private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(20.0);

	private static readonly TimeSpan PayoutOperationTimeout = TimeSpan.FromSeconds(28.0);

	private static readonly TimeSpan PayoutRequestTimeout = TimeSpan.FromSeconds(25.0);

	public DashboardApiClient(AuthManager auth)
	{
		_auth = auth;
	}

	public Task<JsonElement> GetDashboardAsync(CancellationToken cancellationToken = default(CancellationToken), bool includeCommunications = true, string? tz = null)
	{
		return InvokeAsync("get_dashboard", new DashboardParams
		{
			IncludeCommunications = includeCommunications,
			Tz = tz
		}, cancellationToken);
	}

	public Task<JsonElement> GetCommunicationsAsync(string? knownRevision, CancellationToken cancellationToken = default(CancellationToken))
	{
		return InvokeAsync("get_communications", new
		{
			known_revision = knownRevision
		}, cancellationToken);
	}

	public Task<JsonElement> PresentGuardNoticeAsync(string noticeId, string? contentRevision, CancellationToken cancellationToken = default(CancellationToken))
	{
		return InvokeAsync("present_guard_notice", new
		{
			notice_id = noticeId,
			content_revision = contentRevision
		}, cancellationToken);
	}

	public Task<JsonElement> GetReferralAsync(CancellationToken cancellationToken = default(CancellationToken))
	{
		return InvokeAsync("get_referral", EmptyParams, cancellationToken);
	}

	public Task<JsonElement> ListDaysAsync(int offset, string? tz, CancellationToken cancellationToken = default(CancellationToken))
	{
		return InvokeAsync("list_days", new HistoryParams
		{
			Limit = 7,
			Offset = Math.Max(0, offset),
			Tz = tz
		}, cancellationToken);
	}

	public Task<JsonElement> ListTransactionsAsync(int offset, string? tz, CancellationToken cancellationToken = default(CancellationToken))
	{
		return InvokeAsync("list_transactions", new HistoryParams
		{
			Limit = 15,
			Offset = Math.Max(0, offset),
			Tz = tz
		}, cancellationToken);
	}

	public Task<JsonElement> RequestPayoutAsync(string method, string? destination, CancellationToken cancellationToken = default(CancellationToken))
	{
		return InvokeAsync("request_payout", new PayoutParams
		{
			Method = method,
			Destination = destination
		}, cancellationToken, PayoutOperationTimeout, PayoutRequestTimeout);
	}

	private async Task<JsonElement> InvokeAsync(string action, object actionParams, CancellationToken cancellationToken, TimeSpan? operationTimeout = null, TimeSpan? requestTimeout = null)
	{
		using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		cts.CancelAfter(operationTimeout ?? OperationTimeout);
		CancellationToken ct = cts.Token;
		string accessToken = await _auth.GetValidTokenAsync(ct).ConfigureAwait(continueOnCapturedContext: false);
		APIClient aPIClient = new APIClient();
		aPIClient.SetAccessToken(accessToken);
		return await aPIClient.PostDashboardActionAsync(action, actionParams, ct, requestTimeout).ConfigureAwait(continueOnCapturedContext: false);
	}
}
