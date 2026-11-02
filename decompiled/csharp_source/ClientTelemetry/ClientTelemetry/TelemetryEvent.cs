using System;
using System.Collections.Generic;
using System.Text;

namespace ClientTelemetry;

public sealed class TelemetryEvent
{
	public string Name { get; }

	public Kind Kind { get; }

	public Severity Severity { get; }

	public Gate Gate { get; }

	public Attributes Attributes { get; }

	public Guid Id { get; internal set; }

	public long Seq { get; internal set; }

	public DateTimeOffset Time { get; internal set; }

	public long MonoMs { get; internal set; }

	public string? RecordingId { get; internal set; }

	public int? SegmentIndex { get; internal set; }

	public int Repeats { get; internal set; }

	public TelemetryEvent(string name, Kind kind, Severity severity, Gate gate, Attributes attributes)
	{
		Name = name;
		Kind = kind;
		Severity = severity;
		Gate = gate;
		Attributes = attributes;
	}

	internal string DedupeKey()
	{
		StringBuilder stringBuilder = new StringBuilder(Name);
		foreach (var (value, obj2) in Attributes.Items)
		{
			if (obj2 is string value2)
			{
				stringBuilder.Append('|').Append(value).Append('=')
					.Append(value2);
			}
		}
		return stringBuilder.ToString();
	}
}
