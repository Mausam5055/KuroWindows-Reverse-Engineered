using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DeviceIdentity;
using KuroWindows.Input;
using KuroWindows.RecorderCore.Segments;

namespace KuroWindows.Capture;

internal sealed class SegmentSessionFactsCollector
{
	private const long ProbeMinIntervalMs = 60000L;

	private readonly Func<InputFactsSnapshot?> _snapshotInput;

	private readonly object _sync = new object();

	private SegmentSessionEnvironment? _session;

	private IReadOnlyList<SegmentDisplayFact>? _displays;

	private long _lastProbeCompletedTicks;

	private int _probeRunning;

	public SegmentSessionFactsCollector(Func<InputFactsSnapshot?> snapshotInput)
	{
		_snapshotInput = snapshotInput;
	}

	public void SegmentOpened()
	{
		try
		{
			_snapshotInput();
		}
		catch
		{
		}
		RefreshEnvironment();
	}

	public SegmentSessionFacts? SegmentClosed()
	{
		InputFactsSnapshot inputFactsSnapshot = null;
		try
		{
			inputFactsSnapshot = _snapshotInput();
		}
		catch
		{
		}
		SegmentSessionEnvironment session;
		IReadOnlyList<SegmentDisplayFact> displays;
		lock (_sync)
		{
			session = _session;
			displays = _displays;
		}
		if (inputFactsSnapshot == null && session == null && displays == null)
		{
			return null;
		}
		return new SegmentSessionFacts
		{
			Input = ((inputFactsSnapshot == null) ? null : ToManifest(inputFactsSnapshot)),
			Session = ((session == null) ? null : new SegmentSessionEnvironment
			{
				Remote = session.Remote,
				ConnectState = session.ConnectState,
				RemoteTools = ((session.RemoteTools == null) ? null : new List<string>(session.RemoteTools))
			}),
			Displays = ((displays == null) ? null : displays.Select(Copy).ToList())
		};
	}

	private static SegmentInputFacts ToManifest(InputFactsSnapshot input)
	{
		return new SegmentInputFacts
		{
			Events = input.Events,
			Injected = input.Injected,
			InjectedMouse = input.InjectedMouse,
			InjectedKey = input.InjectedKey,
			ExtraInfo = ((input.ExtraInfo.Count == 0) ? null : (from e in input.ExtraInfo.Take(4)
				select new SegmentInputExtraInfo
				{
					Tag = e.Tag,
					N = e.Count
				}).ToList()),
			RawPackets = input.RawPackets,
			RawNullDevice = input.RawNullDevice,
			RawAbsolute = input.RawAbsolute,
			Devices = ((input.Devices.Count == 0) ? null : (from d in input.Devices.Take(6)
				select new SegmentInputDevice
				{
					Vid = d.Vid,
					Pid = d.Pid,
					Kind = d.Kind,
					Name = d.Name
				}).ToList()),
			RawKeyboard = input.RawKeyboard,
			RawFailures = input.RawFailures,
			GamepadPackets = input.GamepadPackets,
			Rearms = input.Rearms,
			GapMs = input.GapMs,
			Touchpad = ((input.Touchpad == null) ? null : new SegmentTouchpadFacts
			{
				Devices = input.Touchpad.Devices,
				Frames = input.Touchpad.Frames,
				Reports = input.Touchpad.Reports,
				Partial = input.Touchpad.Partial,
				Timeouts = input.Touchpad.Timeouts,
				DupIds = input.Touchpad.DupIds,
				UnknownReportId = input.Touchpad.UnknownReportId,
				UnknownDevice = input.Touchpad.UnknownDevice,
				LenMismatch = input.Touchpad.LenMismatch,
				RawFail = input.Touchpad.RawFail,
				BufferGrow = input.Touchpad.BufferGrow,
				ContactsTruncated = input.Touchpad.ContactsTruncated,
				Gestures = new SegmentTouchpadGestureFacts
				{
					Scroll = input.Touchpad.GestureScroll,
					Pinch = input.Touchpad.GesturePinch,
					Swipe = input.Touchpad.GestureSwipe,
					Pan = input.Touchpad.GesturePan,
					Other = input.Touchpad.GestureOther
				},
				ScrollRows = input.Touchpad.ScrollRows,
				OsRows = input.Touchpad.OsRows,
				OrphanWheels = input.Touchpad.OrphanWheels,
				PalmContacts = input.Touchpad.PalmContacts,
				Registered = input.Touchpad.Registered,
				RegisterFail = input.Touchpad.RegisterFail,
				Reregisters = input.Touchpad.Reregisters
			})
		};
	}

	private static SegmentDisplayFact Copy(SegmentDisplayFact d)
	{
		return new SegmentDisplayFact
		{
			Tech = d.Tech,
			Mfr = d.Mfr,
			Pid = d.Pid,
			Serial = d.Serial,
			Adapter = d.Adapter,
			Builtin = d.Builtin
		};
	}

	public void RefreshEnvironment()
	{
		long num = Interlocked.Read(in _lastProbeCompletedTicks);
		if ((num != 0L && Stopwatch.GetElapsedTime(num).TotalMilliseconds < 60000.0) || Interlocked.Exchange(ref _probeRunning, 1) != 0)
		{
			return;
		}
		Task.Run(() =>
		{
			try
			{
				SegmentSessionEnvironment session = new SegmentSessionEnvironment
				{
					Remote = RemoteControlProbe.IsRemoteSession,
					ConnectState = HardwareFacets.Text(ConnectState(), 20),
					RemoteTools = (from t in (from t in RemoteControlProbe.Scan()
							select HardwareFacets.Text(t, 32) into t
							where t != null
							select t).Take(8)
						select (t)).ToList()
				};
				List<SegmentDisplayFact> list = (from d in DisplayFacts.Enumerate().Take(4)
					select new SegmentDisplayFact
					{
						Tech = d.Tech,
						Mfr = d.Mfr,
						Pid = d.Pid,
						Serial = d.Serial,
						Adapter = d.Adapter,
						Builtin = d.Builtin
					}).ToList();
				lock (_sync)
				{
					_session = session;
					_displays = ((list.Count == 0) ? null : list);
				}
				Interlocked.Exchange(ref _lastProbeCompletedTicks, Stopwatch.GetTimestamp());
			}
			catch (Exception ex)
			{
				KuroLog.LocalOnly("capture", "session facts probe failed: " + ex.GetType().Name + ": " + ex.Message, "RefreshEnvironment", "D:\\a\\markov-screen-recorder-tool\\markov-screen-recorder-tool\\kuro\\windows\\src\\KuroWindows\\Capture\\SegmentSessionFactsCollector.cs", 221);
			}
			finally
			{
				Interlocked.Exchange(ref _probeRunning, 0);
			}
		});
	}

	private static string ConnectState()
	{
		try
		{
			return RemoteSessionProbe.SessionStateLogValue;
		}
		catch
		{
			return "console";
		}
	}
}
