using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using KuroWindows.Input;
using KuroWindows.RecorderCore.Encode;

namespace KuroWindows.Capture;

public sealed class FrameSidecarWriter : IDisposable
{
	private sealed class FrameRecord
	{
		public int SidecarSchema { get; set; } = 2;

		public long Frame { get; set; }

		public long WallMs { get; set; }

		public long PtsNs { get; set; }

		public long DurationNs { get; set; }

		public string FrameKind { get; set; } = "";

		public string TimestampKind { get; set; } = "";

		public string TimestampQuality { get; set; } = "";

		public int FramesDroppedSincePrev { get; set; }

		public bool? CursorBlendChanged { get; set; }

		public MouseField Mouse { get; set; } = new MouseField();

		public uint[] KeysDown { get; set; } = Array.Empty<uint>();

		public uint Modifiers { get; set; }

		public ActiveAppField? ActiveApp { get; set; }

		public ScrollField[]? ScrollThisFrame { get; set; }

		public ClickField[]? ClicksThisFrame { get; set; }

		public KeyTransitionField[]? KeyTransitionsThisFrame { get; set; }

		public SystemStateField State { get; set; } = new SystemStateField();
	}

	private sealed class MouseField
	{
		public double X { get; set; }

		public double Y { get; set; }

		public int Screen { get; set; }

		public MouseButtonsField Buttons { get; set; } = new MouseButtonsField();

		public bool Visible { get; set; }

		public bool Suppressed { get; set; }

		public string Shape { get; set; } = "hidden";
	}

	private sealed class MouseButtonsField
	{
		public bool Left { get; set; }

		public bool Right { get; set; }

		public bool Middle { get; set; }

		[JsonPropertyName("other_3")]
		public bool Other3 { get; set; }

		[JsonPropertyName("other_4")]
		public bool Other4 { get; set; }
	}

	private sealed class ActiveAppField
	{
		public string Name { get; set; } = "";

		public string? ExeName { get; set; }

		public int Pid { get; set; }

		public string? WindowTitle { get; set; }

		public string? BrowserUrl { get; set; }

		public bool? IsFullscreen { get; set; }
	}

	private sealed class ScrollField
	{
		public double Dx { get; set; }

		public double Dy { get; set; }

		public int Screen { get; set; }

		public long T { get; set; }

		public string? Src { get; set; }
	}

	private sealed class ClickField
	{
		public string Button { get; set; } = "";

		public bool IsDown { get; set; }

		public int X { get; set; }

		public int Y { get; set; }

		public int Screen { get; set; }

		public long T { get; set; }
	}

	private sealed class KeyTransitionField
	{
		public uint KeyCode { get; set; }

		public string? Chars { get; set; }

		public bool IsDown { get; set; }

		public int? Screen { get; set; }

		public long T { get; set; }
	}

	private sealed class SystemStateField
	{
		public string? Appearance { get; set; }

		public int? BatteryLevel { get; set; }

		public bool? IsCharging { get; set; }

		public bool? IsOnAc { get; set; }

		public bool? IsOnline { get; set; }

		public string? NetworkType { get; set; }

		public string? MemoryPressure { get; set; }

		public string? InputSourceName { get; set; }

		public string? InputSourceId { get; set; }

		public bool MicrophoneActive { get; set; }

		public bool CameraActive { get; set; }
	}

	internal const int SchemaVersion = 2;

	private static readonly JsonSerializerOptions Opts = new JsonSerializerOptions
	{
		PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
	};

	private readonly StreamWriter _writer;

	private readonly object _lock = new object();

	public FrameSidecarWriter(string path)
	{
		_writer = new StreamWriter(path, append: false, Encoding.UTF8)
		{
			AutoFlush = true
		};
	}

	internal void WriteCommit(EncodedFrameCommit commit, double mouseX, double mouseY, int mouseScreen, FrameStateSnapshot state, int framesDroppedSincePrev = 0, bool? cursorBlendChanged = null, CursorFrameSample cursor = default(CursorFrameSample))
	{
		WriteRecord(commit, mouseX, mouseY, mouseScreen, state, framesDroppedSincePrev, cursorBlendChanged, in cursor);
	}

	private void WriteRecord(EncodedFrameCommit commit, double mouseX, double mouseY, int mouseScreen, FrameStateSnapshot state, int framesDroppedSincePrev, bool? cursorBlendChanged, in CursorFrameSample cursor)
	{
		string value = JsonSerializer.Serialize(new FrameRecord
		{
			Frame = commit.FrameIndex,
			WallMs = commit.WallMs,
			PtsNs = commit.PtsNs,
			DurationNs = commit.DurationNs,
			FrameKind = ToSnake(commit.FrameKind.ToString()),
			TimestampKind = ToSnake(commit.TimestampKind.ToString()),
			TimestampQuality = ToSnake(commit.Quality.ToString()),
			FramesDroppedSincePrev = framesDroppedSincePrev,
			CursorBlendChanged = cursorBlendChanged,
			Mouse = new MouseField
			{
				X = mouseX,
				Y = mouseY,
				Screen = mouseScreen,
				Buttons = new MouseButtonsField
				{
					Left = state.LeftDown,
					Right = state.RightDown,
					Middle = state.MiddleDown,
					Other3 = state.Other3Down,
					Other4 = state.Other4Down
				},
				Visible = cursor.Visible,
				Suppressed = cursor.Suppressed,
				Shape = (cursor.Shape ?? "hidden")
			},
			KeysDown = state.KeysDown,
			Modifiers = state.Modifiers,
			ActiveApp = ((state.ActiveApp == null) ? null : new ActiveAppField
			{
				Name = state.ActiveApp.Name,
				ExeName = state.ActiveApp.ExeName,
				Pid = state.ActiveApp.Pid,
				WindowTitle = state.ActiveApp.WindowTitle,
				BrowserUrl = state.ActiveApp.BrowserUrl,
				IsFullscreen = state.ActiveApp.IsFullScreen
			}),
			ScrollThisFrame = ((state.Scroll.Length == 0) ? null : state.Scroll.Select((ScrollEntry s) => new ScrollField
			{
				Dx = s.Dx,
				Dy = s.Dy,
				Screen = s.Screen,
				T = s.TimestampMs,
				Src = s.Src
			}).ToArray()),
			ClicksThisFrame = ((state.Clicks.Length == 0) ? null : state.Clicks.Select((ClickEntry c) => new ClickField
			{
				Button = c.Button,
				IsDown = c.IsDown,
				X = c.X,
				Y = c.Y,
				Screen = c.Screen,
				T = c.TimestampMs
			}).ToArray()),
			KeyTransitionsThisFrame = ((state.KeyTransitions.Length == 0) ? null : state.KeyTransitions.Select((KeyTransitionEntry k) => new KeyTransitionField
			{
				KeyCode = k.KeyCode,
				Chars = k.Chars,
				IsDown = k.IsDown,
				Screen = k.Screen,
				T = k.TimestampMs
			}).ToArray()),
			State = new SystemStateField
			{
				Appearance = state.Appearance,
				BatteryLevel = state.BatteryLevel,
				IsCharging = state.IsCharging,
				IsOnAc = state.IsOnAc,
				IsOnline = state.IsOnline,
				NetworkType = state.NetworkInterfaceType,
				MemoryPressure = state.MemoryPressure,
				InputSourceName = state.InputSourceName,
				InputSourceId = state.InputSourceId,
				MicrophoneActive = state.MicrophoneActive,
				CameraActive = state.CameraActive
			}
		}, Opts);
		lock (_lock)
		{
			_writer.WriteLine(value);
		}
	}

	private static string ToSnake(string value)
	{
		if (string.IsNullOrEmpty(value))
		{
			return value;
		}
		List<char> list = new List<char>(value.Length + 4);
		for (int i = 0; i < value.Length; i++)
		{
			char c = value[i];
			if (char.IsUpper(c) && i > 0)
			{
				list.Add('_');
			}
			list.Add(char.ToLowerInvariant(c));
		}
		return new string(list.ToArray());
	}

	public void Dispose()
	{
		lock (_lock)
		{
			try
			{
				_writer.Flush();
			}
			catch
			{
			}
			_writer.Dispose();
		}
	}
}
