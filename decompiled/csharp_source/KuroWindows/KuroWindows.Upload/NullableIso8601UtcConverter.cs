using System;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KuroWindows.Upload;

internal sealed class NullableIso8601UtcConverter : JsonConverter<DateTimeOffset?>
{
	private const string Fmt = "yyyy-MM-dd'T'HH:mm:ss'Z'";

	public override DateTimeOffset? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
	{
		if (reader.TokenType != JsonTokenType.Null)
		{
			return DateTimeOffset.Parse(reader.GetString(), CultureInfo.InvariantCulture);
		}
		return null;
	}

	public override void Write(Utf8JsonWriter writer, DateTimeOffset? value, JsonSerializerOptions options)
	{
		if (value.HasValue)
		{
			writer.WriteStringValue(value.Value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
		}
		else
		{
			writer.WriteNullValue();
		}
	}
}
