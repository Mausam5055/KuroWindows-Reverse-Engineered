using System;
using System.Threading;

namespace KuroWindows.Input.Touchpad;

internal sealed record TouchpadDeviceRecord
{
	private sealed class PreparsedClaim
	{
		public int Written;
	}

	private readonly PreparsedClaim _claim = new PreparsedClaim();

	public bool Present { get; init; }

	public bool LegacyPresent { get; init; }

	public int Touchscreens { get; init; }

	public int Pens { get; init; }

	public string Arch { get; init; } = "";

	public int Device { get; init; }

	public string Vid { get; init; } = "";

	public string Pid { get; init; } = "";

	public int Version { get; init; }

	public string Iface { get; init; } = "";

	public bool Registered { get; init; }

	public int RegisterError { get; init; }

	public int LogicalXMin { get; init; }

	public int LogicalXMax { get; init; }

	public int LogicalYMin { get; init; }

	public int LogicalYMax { get; init; }

	public int PhysicalXMin { get; init; }

	public int PhysicalXMax { get; init; }

	public int PhysicalYMin { get; init; }

	public int PhysicalYMax { get; init; }

	public uint Units { get; init; }

	public int UnitsExponent { get; init; }

	public double MmPerCountX { get; init; }

	public double MmPerCountY { get; init; }

	public string MmPerCountSource { get; init; } = "";

	public double WidthMm { get; init; }

	public double HeightMm { get; init; }

	public int Slots { get; init; }

	public int ReportId { get; init; }

	public int ReportBytes { get; init; }

	public string[] Usages { get; init; } = Array.Empty<string>();

	public bool? ConfidenceUsable { get; init; }

	public string? ReportMode { get; init; }

	public int? RateHz { get; init; }

	public string PreparsedSha256 { get; init; } = "";

	public int PreparsedBytes { get; init; }

	public string PreparsedBase64 { get; init; } = "";

	public bool Reversed { get; init; }

	public string ScrollDirectionSource { get; init; } = "";

	public bool PanEnabled { get; init; }

	public bool ZoomEnabled { get; init; }

	public bool? TouchpadActive { get; init; }

	public bool? ExternalMousePresent { get; init; }

	private static readonly string[] ContactFields = new string[5] { "id", "x", "y", "tip", "conf" };

	private static readonly object Algo = new
	{
		id = "ptp-scroll-1",
		k = 1.0,
		gainFormula = "k*dpi/25.4",
		decideMm = 1.5,
		undecidedMm = 1.0,
		boxMm = new int[2] { 40, 7 },
		boxMs = 150,
		thumbMm = 4.0,
		pinchMs = 300,
		debounceMs = 100,
		liftMs = 100,
		swipeMm = 5,
		coalesceMs = 16,
		singleFingerMs = 40
	};

	public int? MaxContacts { get; init; }

	public static TouchpadDeviceRecord Absent(int touchscreens, int pens, bool legacyPresent, string arch)
	{
		return new TouchpadDeviceRecord
		{
			Present = false,
			Touchscreens = touchscreens,
			Pens = pens,
			LegacyPresent = legacyPresent,
			Arch = arch
		};
	}

	public static TouchpadDeviceRecord Of(int device, PtpDevicePlan plan, TouchpadSettingsSnapshot settings, int touchscreens, int pens, string arch, bool registered, int registerError)
	{
		return new TouchpadDeviceRecord
		{
			Present = true,
			Device = device,
			Vid = plan.Vid,
			Pid = plan.Pid,
			Version = plan.Version,
			Iface = plan.Iface,
			Registered = registered,
			RegisterError = registerError,
			LogicalXMin = plan.LogicalXMin,
			LogicalXMax = plan.LogicalXMax,
			LogicalYMin = plan.LogicalYMin,
			LogicalYMax = plan.LogicalYMax,
			PhysicalXMin = plan.PhysicalXMin,
			PhysicalXMax = plan.PhysicalXMax,
			PhysicalYMin = plan.PhysicalYMin,
			PhysicalYMax = plan.PhysicalYMax,
			Units = plan.Units,
			UnitsExponent = plan.UnitsExponent,
			MmPerCountX = plan.MmPerCountX,
			MmPerCountY = plan.MmPerCountY,
			MmPerCountSource = plan.MmPerCountSource,
			WidthMm = plan.WidthMm,
			HeightMm = plan.HeightMm,
			Slots = plan.Slots,
			ReportId = plan.ReportId,
			ReportBytes = plan.ReportBytes,
			Usages = plan.Usages,
			ConfidenceUsable = (plan.HasConfidence ? ((bool?)null) : new bool?(false)),
			PreparsedSha256 = plan.PreparsedSha256,
			PreparsedBytes = plan.PreparsedBytes,
			PreparsedBase64 = plan.PreparsedBase64,
			Touchscreens = touchscreens,
			Pens = pens,
			Arch = arch,
			LegacyPresent = (settings.LegacyPresent == true)
		}.WithSettings(settings);
	}

	public TouchpadDeviceRecord WithSettings(TouchpadSettingsSnapshot settings)
	{
		return this with
		{
			Reversed = settings.Reversed,
			ScrollDirectionSource = settings.Source,
			PanEnabled = settings.Pan,
			ZoomEnabled = settings.Zoom,
			TouchpadActive = settings.Active,
			ExternalMousePresent = settings.ExternalMouse,
			MaxContacts = settings.MaxContacts
		};
	}

	public bool ClaimPreparsed()
	{
		return Interlocked.Exchange(ref _claim.Written, 1) == 0;
	}

	public object ToRow(string evt, long timestamp, bool includePreparsed)
	{
		if (!Present)
		{
			return new
			{
				type = "touchpad_device",
				@event = evt,
				present = false,
				legacyPresent = LegacyPresent,
				touchscreens = Touchscreens,
				pens = Pens,
				arch = Arch,
				timestamp = timestamp
			};
		}
		return new
		{
			type = "touchpad_device",
			@event = evt,
			device = Device,
			vid = Vid,
			pid = Pid,
			version = Version,
			iface = Iface,
			present = true,
			registered = Registered,
			registerError = (Registered ? ((int?)null) : new int?(RegisterError)),
			logical = new
			{
				xMin = LogicalXMin,
				xMax = LogicalXMax,
				yMin = LogicalYMin,
				yMax = LogicalYMax
			},
			physical = new
			{
				xMin = PhysicalXMin,
				xMax = PhysicalXMax,
				yMin = PhysicalYMin,
				yMax = PhysicalYMax,
				units = Units,
				exp = UnitsExponent
			},
			mmPerCount = new
			{
				x = MmPerCountX,
				y = MmPerCountY,
				source = MmPerCountSource
			},
			sizeMm = new
			{
				w = WidthMm,
				h = HeightMm
			},
			slots = Slots,
			reportId = ReportId,
			reportBytes = ReportBytes,
			usages = Usages,
			contactFields = ContactFields,
			confidenceUsable = ConfidenceUsable,
			reportMode = ReportMode,
			rateHz = RateHz,
			preparsedSha256 = PreparsedSha256,
			preparsedBytes = PreparsedBytes,
			preparsed = (includePreparsed ? PreparsedBase64 : null),
			scrollDirection = new
			{
				reversed = Reversed,
				source = ScrollDirectionSource
			},
			panEnabled = PanEnabled,
			zoomEnabled = ZoomEnabled,
			touchpadActive = TouchpadActive,
			externalMousePresent = ExternalMousePresent,
			maxContacts = MaxContacts,
			algo = Algo,
			touchscreens = Touchscreens,
			pens = Pens,
			arch = Arch,
			timestamp = timestamp
		};
	}
}
