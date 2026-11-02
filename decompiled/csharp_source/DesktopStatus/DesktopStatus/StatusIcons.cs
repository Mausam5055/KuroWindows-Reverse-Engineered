using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;

namespace DesktopStatus;

public sealed class StatusIcons : IDisposable
{
	private enum StatusState
	{
		Idle,
		Recording,
		Privacy,
		Attention
	}

	private sealed class TrayIcon : IDisposable
	{
		public Icon Icon { get; }

		private TrayIcon(Icon icon)
		{
			Icon = icon;
		}

		public static TrayIcon Load(string name)
		{
			using Stream bitmapStream = (System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Resources/" + name)) ?? throw new FileNotFoundException("Missing status icon resource: " + name)).Stream;
			IconBitmapDecoder iconBitmapDecoder = new IconBitmapDecoder(bitmapStream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
			int target = SystemInformation.SmallIconSize.Width;
			BitmapFrame source = (from f in iconBitmapDecoder.Frames
				orderby Math.Abs(f.PixelWidth - target), f.PixelWidth descending
				select f).First();
			PngBitmapEncoder pngBitmapEncoder = new PngBitmapEncoder();
			pngBitmapEncoder.Frames.Add(BitmapFrame.Create(source));
			using MemoryStream memoryStream = new MemoryStream();
			pngBitmapEncoder.Save(memoryStream);
			memoryStream.Position = 0L;
			using Bitmap bitmap = new Bitmap(memoryStream);
			nint hicon = bitmap.GetHicon();
			try
			{
				return new TrayIcon((System.Drawing.Icon.FromHandle(hicon).Clone() as Icon) ?? throw new InvalidOperationException("Could not create the tray icon."));
			}
			finally
			{
				DestroyIcon(hicon);
			}
		}

		[DllImport("user32.dll", SetLastError = true)]
		[return: MarshalAs(UnmanagedType.Bool)]
		private static extern bool DestroyIcon(nint handle);

		public void Dispose()
		{
			Icon.Dispose();
		}
	}

	private static readonly StatusState[] States = new StatusState[4]
	{
		StatusState.Idle,
		StatusState.Recording,
		StatusState.Privacy,
		StatusState.Attention
	};

	private readonly string _product;

	private readonly Action<string> _log;

	private readonly ImageSource _windowIcon;

	private readonly Dictionary<StatusState, TrayIcon> _trayIcons = new Dictionary<StatusState, TrayIcon>();

	private readonly Dictionary<StatusState, ImageSource> _taskbarBadges = new Dictionary<StatusState, ImageSource>();

	private NotifyIcon? _trayIcon;

	private Window? _window;

	private StatusState _session;

	private bool _selfApp;

	private bool _attention;

	private bool _disposed;

	public Icon CurrentTrayIcon => _trayIcons[Shown].Icon;

	private StatusState Shown
	{
		get
		{
			if (!_attention)
			{
				return _session;
			}
			return StatusState.Attention;
		}
	}

	public StatusIcons(string product, string appIcon, Action<string> log)
	{
		_product = product;
		_log = log;
		_windowIcon = LoadImage(appIcon);
		StatusState[] states = States;
		foreach (StatusState statusState in states)
		{
			_trayIcons[statusState] = TrayIcon.Load("status-" + Name(statusState) + ".ico");
			_taskbarBadges[statusState] = LoadImage("status-badge-" + Name(statusState) + ".ico");
		}
	}

	public void AttachTrayIcon(NotifyIcon trayIcon)
	{
		if (!_disposed)
		{
			_trayIcon = trayIcon;
			Apply();
		}
	}

	public void DetachTrayIcon(NotifyIcon trayIcon)
	{
		if (_trayIcon == trayIcon)
		{
			_trayIcon = null;
		}
	}

	public void AttachWindow(Window window)
	{
		if (_disposed)
		{
			return;
		}
		if (_window != window)
		{
			if (_window != null)
			{
				_window.SourceInitialized -= OnWindowSourceInitialized;
			}
			_window = window;
			_window.SourceInitialized += OnWindowSourceInitialized;
		}
		Apply();
	}

	public void DetachWindow(Window window)
	{
		if (_window == window)
		{
			_window.SourceInitialized -= OnWindowSourceInitialized;
			_window = null;
		}
	}

	public void SetSession(bool recording, bool privacyPaused, string? privacyContextType)
	{
		if (!_disposed)
		{
			StatusState statusState = (privacyPaused ? StatusState.Privacy : (recording ? StatusState.Recording : StatusState.Idle));
			bool flag = statusState == StatusState.Privacy && string.Equals(privacyContextType, "self_app", StringComparison.OrdinalIgnoreCase);
			if (statusState != _session || flag != _selfApp)
			{
				_session = statusState;
				_selfApp = flag;
				Apply();
			}
		}
	}

	public void SetAttention(bool attention)
	{
		if (!_disposed && _attention != attention)
		{
			_attention = attention;
			Apply();
		}
	}

	private void Apply()
	{
		if (_disposed)
		{
			return;
		}
		StatusState shown = Shown;
		string text = Tooltip(shown);
		if (_trayIcon != null)
		{
			try
			{
				_trayIcon.Icon = _trayIcons[shown].Icon;
				_trayIcon.Text = text;
			}
			catch (Exception ex)
			{
				_log("Tray icon update failed; detaching stale notify icon: " + ex.Message);
				_trayIcon = null;
			}
		}
		if (_window == null)
		{
			return;
		}
		try
		{
			_window.Icon = _windowIcon;
			Window window = _window;
			if (window.TaskbarItemInfo == null)
			{
				TaskbarItemInfo taskbarItemInfo = (window.TaskbarItemInfo = new TaskbarItemInfo());
			}
			_window.TaskbarItemInfo.Overlay = _taskbarBadges[shown];
			_window.TaskbarItemInfo.Description = text;
		}
		catch (Exception ex2)
		{
			_log("Taskbar icon update failed; detaching stale window: " + ex2.Message);
			_window = null;
		}
	}

	private void OnWindowSourceInitialized(object? sender, EventArgs e)
	{
		Apply();
	}

	private string Tooltip(StatusState state)
	{
		return state switch
		{
			StatusState.Recording => _product + " - recording", 
			StatusState.Attention => _product + " - close other recording apps", 
			StatusState.Privacy => _selfApp ? (_product + " - hidden while " + _product + " is open") : (_product + " - paused for privacy"), 
			_ => _product, 
		};
	}

	private static string Name(StatusState state)
	{
		return state.ToString().ToLowerInvariant();
	}

	private static ImageSource LoadImage(string name)
	{
		BitmapFrame bitmapFrame = BitmapFrame.Create(new Uri("pack://application:,,,/Resources/" + name));
		bitmapFrame.Freeze();
		return bitmapFrame;
	}

	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}
		_disposed = true;
		if (_window != null)
		{
			_window.SourceInitialized -= OnWindowSourceInitialized;
		}
		_trayIcon = null;
		_window = null;
		foreach (TrayIcon value in _trayIcons.Values)
		{
			value.Dispose();
		}
		_trayIcons.Clear();
		_taskbarBadges.Clear();
	}
}
