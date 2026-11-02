using System.Text.Json.Serialization;

namespace KuroWindows.CoreBridge;

public sealed record CoreBridgeResponse
{
	[JsonPropertyName("id")]
	public string? Id { get; init; }

	[JsonPropertyName("ok")]
	public bool Ok { get; init; }

	[JsonPropertyName("result")]
	public object? Result { get; init; }

	[JsonPropertyName("error")]
	public CoreBridgeError? Error { get; init; }

	public static CoreBridgeResponse Success(string? id, object? result = null)
	{
		return new CoreBridgeResponse
		{
			Id = id,
			Ok = true,
			Result = result
		};
	}

	public static CoreBridgeResponse Failure(string? id, string code, string message)
	{
		return new CoreBridgeResponse
		{
			Id = id,
			Ok = false,
			Error = new CoreBridgeError(code, message)
		};
	}
}
