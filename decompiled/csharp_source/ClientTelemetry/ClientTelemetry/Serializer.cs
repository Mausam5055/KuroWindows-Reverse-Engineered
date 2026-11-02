using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text.Json;

namespace ClientTelemetry;

internal static class Serializer
{
	public const int SchemaVersion = 1;

	private static readonly JsonWriterOptions WriterOptions = new JsonWriterOptions
	{
		Indented = false,
		SkipValidation = false
	};

	public static byte[] Event(TelemetryEvent e)
	{
		ArrayBufferWriter<byte> arrayBufferWriter = new ArrayBufferWriter<byte>(256);
		using Utf8JsonWriter utf8JsonWriter = new Utf8JsonWriter(arrayBufferWriter, WriterOptions);
		WriteEvent(utf8JsonWriter, e);
		utf8JsonWriter.Flush();
		return arrayBufferWriter.WrittenSpan.ToArray();
	}

	public static byte[] Events(IReadOnlyList<byte[]> events)
	{
		int num = 1;
		foreach (byte[] @event in events)
		{
			num += @event.Length + 1;
		}
		byte[] array = new byte[num];
		array[0] = 91;
		int num2 = 1;
		for (int i = 0; i < events.Count; i++)
		{
			events[i].CopyTo(array, num2);
			num2 += events[i].Length;
			array[num2++] = (byte)((i == events.Count - 1) ? 93u : 44u);
		}
		return array;
	}

	public static byte[] Body(BatchIdentity id, BatchHeader h, ReadOnlySpan<byte> eventsArray)
	{
		ArrayBufferWriter<byte> arrayBufferWriter = new ArrayBufferWriter<byte>(eventsArray.Length + 1024);
		using (Utf8JsonWriter utf8JsonWriter = new Utf8JsonWriter(arrayBufferWriter, WriterOptions))
		{
			utf8JsonWriter.WriteStartObject();
			utf8JsonWriter.WriteNumber("schema_version", 1);
			utf8JsonWriter.WriteString("batch_id", h.BatchId);
			utf8JsonWriter.WriteString("sent_at", h.SentAt.UtcDateTime.ToString("O"));
			utf8JsonWriter.WriteString("platform", id.Platform);
			utf8JsonWriter.WriteStartObject("app");
			utf8JsonWriter.WriteString("name", id.AppName);
			utf8JsonWriter.WriteString("version", id.AppVersion);
			utf8JsonWriter.WriteEndObject();
			utf8JsonWriter.WriteStartObject("os");
			utf8JsonWriter.WriteString("type", id.Platform);
			if (id.OsVersion != null)
			{
				utf8JsonWriter.WriteString("version", id.OsVersion);
			}
			if (id.OsBuild != null)
			{
				utf8JsonWriter.WriteString("build", id.OsBuild);
			}
			if (id.Arch != null)
			{
				utf8JsonWriter.WriteString("arch", id.Arch);
			}
			utf8JsonWriter.WriteEndObject();
			utf8JsonWriter.WriteString("install_id", id.InstallId);
			WriteNullable(utf8JsonWriter, "user_id", h.UserId);
			WriteNullable(utf8JsonWriter, "device_id", h.DeviceId);
			WriteNullable(utf8JsonWriter, "organization_id", h.OrganizationId);
			utf8JsonWriter.WriteStartObject("health");
			foreach (var (propertyName, value) in h.Health)
			{
				utf8JsonWriter.WriteNumber(propertyName, value);
			}
			utf8JsonWriter.WriteEndObject();
			utf8JsonWriter.WritePropertyName("events");
			utf8JsonWriter.Flush();
		}
		arrayBufferWriter.Write(eventsArray);
		arrayBufferWriter.Write("}"u8);
		return arrayBufferWriter.WrittenSpan.ToArray();
	}

	private static void WriteEvent(Utf8JsonWriter w, TelemetryEvent e)
	{
		w.WriteStartObject();
		w.WriteString("name", e.Name);
		w.WriteString("id", e.Id);
		w.WriteNumber("seq", e.Seq);
		w.WriteString("time", e.Time.UtcDateTime.ToString("O"));
		w.WriteNumber("mono_ms", e.MonoMs);
		w.WriteNumber("severity", (int)e.Severity);
		w.WriteString("kind", WireName(e.Kind));
		w.WriteString("gate", WireName(e.Gate));
		if (e.RecordingId != null)
		{
			w.WriteString("recording_id", e.RecordingId);
		}
		int? segmentIndex = e.SegmentIndex;
		if (segmentIndex.HasValue)
		{
			int valueOrDefault = segmentIndex.GetValueOrDefault();
			w.WriteNumber("segment_index", valueOrDefault);
		}
		if (e.Repeats > 0)
		{
			w.WriteNumber("repeats", e.Repeats);
		}
		w.WriteStartObject("attrs");
		foreach (var (name, value) in e.Attributes.Items)
		{
			WriteValue(w, name, value);
		}
		w.WriteEndObject();
		w.WriteEndObject();
	}

	private static void WriteValue(Utf8JsonWriter w, string name, object value)
	{
		if (!(value is string value2))
		{
			if (!(value is long value3))
			{
				if (!(value is double value4))
				{
					if (!(value is bool value5))
					{
						if (!(value is Guid value6))
						{
							if (!(value is Text text))
							{
								string key;
								if (!(value is IReadOnlyDictionary<string, object> readOnlyDictionary))
								{
									if (!(value is IReadOnlyDictionary<string, long> readOnlyDictionary2))
									{
										return;
									}
									w.WriteStartObject(name);
									foreach (KeyValuePair<string, long> item in readOnlyDictionary2)
									{
										item.Deconstruct(out key, out var value7);
										string propertyName = key;
										long value8 = value7;
										w.WriteNumber(propertyName, value8);
									}
									w.WriteEndObject();
									return;
								}
								w.WriteStartObject(name);
								foreach (KeyValuePair<string, object> item2 in readOnlyDictionary)
								{
									item2.Deconstruct(out key, out var value9);
									string name2 = key;
									object value10 = value9;
									WriteValue(w, name2, value10);
								}
								w.WriteEndObject();
							}
							else
							{
								w.WriteString(name, text.Value);
							}
						}
						else
						{
							w.WriteString(name, value6);
						}
					}
					else
					{
						w.WriteBoolean(name, value5);
					}
				}
				else
				{
					w.WriteNumber(name, value4);
				}
			}
			else
			{
				w.WriteNumber(name, value3);
			}
		}
		else
		{
			w.WriteString(name, value2);
		}
	}

	private static void WriteNullable(Utf8JsonWriter w, string name, string? value)
	{
		if (value == null)
		{
			w.WriteNull(name);
		}
		else
		{
			w.WriteString(name, value);
		}
	}

	public static string WireName(Kind k)
	{
		return k switch
		{
			Kind.Wide => "wide", 
			Kind.Signal => "signal", 
			_ => "counters", 
		};
	}

	public static string WireName(Gate g)
	{
		return g switch
		{
			Gate.Required => "required", 
			Gate.Standard => "standard", 
			_ => "verbose", 
		};
	}
}
