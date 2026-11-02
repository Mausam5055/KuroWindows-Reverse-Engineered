using System;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KuroWindows.Upload;

internal sealed class Iso8601UtcConverter : JsonConverter<DateTimeOffset>
{
	private const string Fmt = "yyyy-MM-dd'T'HH:mm:ss'Z'";

	public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
	{
		return DateTimeOffset.Parse(reader.GetString(), CultureInfo.InvariantCulture);
	}

	public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options)
	{
		writer.WriteStringValue(value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
	}
}
