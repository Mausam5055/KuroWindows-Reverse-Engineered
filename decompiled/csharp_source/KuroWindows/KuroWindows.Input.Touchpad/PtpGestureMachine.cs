using System;
using System.Collections.Generic;

namespace KuroWindows.Input.Touchpad;

internal sealed class PtpGestureMachine
{
	public enum GestureState
	{
		Idle,
		Pending,
		Scroll,
		Pinch,
		Other
	}

	private readonly struct Finger(int id, double x, double y)
	{
		public readonly int Id = id;

		public readonly double X = x;

		public readonly double Y = y;
	}

	public const string AlgoId = "ptp-scroll-1";

	public const string GainFormula = "k*dpi/25.4";

	public const double K = 1.0;

	public const double DecideMm = 1.5;

	public const double UndecidedMm = 1.0;

	public const double BoxWidthMm = 40.0;

	public const double BoxHeightMm = 7.0;

	public const int BoxMs = 150;

	public const double ThumbMm = 4.0;

	public const int PinchMs = 300;

	public const int DebounceMs = 100;

	public const int LiftMs = 100;

	public const double SwipeMm = 5.0;

	public const int CoalesceMs = 16;

	public const int SingleFingerMs = 40;

	private const long VelocityWindowUs = 50000L;

	private const int MaxFingers = 16;

	private readonly PtpDevicePlan _plan;

	private readonly IPtpGestureSink _sink;

	private readonly Func<int> _nextGestureId;

	private readonly Finger[] _fingers = new Finger[16];

	private readonly Dictionary<int, (double X, double Y)> _initial = new Dictionary<int, (double, double)>();

	private readonly Dictionary<int, (double X, double Y)> _prev = new Dictionary<int, (double, double)>();

	private readonly List<(long Us, double X, double Y)> _ring = new List<(long, double, double)>();

	private int _fingerCount;

	private int _gestureFingers;

	private long _startT;

	private int _pendingCount;

	private long _pendingSince;

	private double _gain;

	private int _sign;

	private bool _scrollRows;

	private double _accX;

	private double _accY;

	private double _totalMmX;

	private double _totalMmY;

	private double _cumX;

	private double _cumY;

	private long _windowStart;

	private long _lastT;

	private long _lastUs;

	private double _dist0;

	private double _pinchDist;

	private double _pinchDxMm;

	private double _pinchDyMm;

	public GestureState State { get; private set; }

	public int GestureId { get; private set; }

	public PtpGestureMachine(PtpDevicePlan plan, IPtpGestureSink sink, Func<int> nextGestureId)
	{
		_plan = plan;
		_sink = sink;
		_nextGestureId = nextGestureId;
	}

	public void OnFrame(PtpFrame frame, double gainPxPerMm, int sign, bool scrollRows, ScrollEndReason liftReason)
	{
		long wallMs = frame.WallMs;
		int num = 0;
		for (int i = 0; i < frame.Count; i++)
		{
			if (num >= 16)
			{
				break;
			}
			PtpContact ptpContact = frame.Contacts[i];
			if (ptpContact.Tip && !ptpContact.Palm)
			{
				_fingers[num++] = new Finger(ptpContact.Id, (double)ptpContact.X * _plan.MmPerCountX, (double)ptpContact.Y * _plan.MmPerCountY);
			}
		}
		if (num == 0)
		{
			EndGesture(wallMs, liftReason);
			_fingerCount = 0;
			_prev.Clear();
			return;
		}
		_gain = gainPxPerMm;
		_sign = sign;
		_lastT = wallMs;
		_lastUs = frame.ScanUs;
		if (num != _fingerCount)
		{
			GestureState state = State;
			bool flag = (uint)state <= 1u;
			if (flag || (State == GestureState.Other && _gestureFingers == 1))
			{
				if (State == GestureState.Pending && num < _fingerCount)
				{
					EndGesture(wallMs, ScrollEndReason.Fingers);
				}
				Adopt(num, wallMs, scrollRows);
			}
			else if (_pendingCount != num)
			{
				_pendingCount = num;
				_pendingSince = wallMs;
			}
			else if (wallMs - _pendingSince >= 100)
			{
				EndGesture(wallMs, ScrollEndReason.Fingers);
				Adopt(num, wallMs, scrollRows);
			}
		}
		else
		{
			_pendingCount = 0;
		}
		if ((frame.Buttons & 1) != 0)
		{
			if (State == GestureState.Scroll)
			{
				EndGesture(wallMs, ScrollEndReason.Button);
				_gestureFingers = 0;
			}
			State = GestureState.Other;
			UpdatePrev(num);
			return;
		}
		switch (State)
		{
		case GestureState.Pending:
			Decide(num, wallMs, scrollRows);
			break;
		case GestureState.Scroll:
			if (wallMs - _startT < 300 && num == 2 && IsPinch())
			{
				EndGesture(wallMs, ScrollEndReason.Pinch, keepInitial: true);
				ToPinch();
			}
			else
			{
				Accumulate(num, frame.ScanUs, wallMs);
			}
			break;
		case GestureState.Pinch:
			if (num >= 2)
			{
				MeasurePinch(num);
			}
			break;
		}
		UpdatePrev(num);
	}

	public void End(long t, ScrollEndReason reason)
	{
		EndGesture(t, reason);
		_fingerCount = 0;
		_prev.Clear();
	}

	private void Adopt(int count, long t, bool scrollRows)
	{
		_fingerCount = count;
		_gestureFingers = count;
		_startT = t;
		_pendingCount = 0;
		_scrollRows = scrollRows;
		_initial.Clear();
		_prev.Clear();
		for (int i = 0; i < count; i++)
		{
			_initial[_fingers[i].Id] = (_fingers[i].X, _fingers[i].Y);
		}
		if (count >= 2)
		{
			GestureId = _nextGestureId();
		}
		State = ((count == 2) ? GestureState.Pending : GestureState.Other);
	}

	private void Decide(int count, long t, bool scrollRows)
	{
		if (count != 2)
		{
			State = GestureState.Other;
			return;
		}
		Finger finger = _fingers[0];
		Finger finger2 = _fingers[1];
		double num = Moved(in finger);
		double num2 = Moved(in finger2);
		if (num < 1.0 && num2 < 1.0)
		{
			return;
		}
		double num3 = Math.Abs(finger.X - finger2.X);
		double num4 = Math.Abs(finger.Y - finger2.Y);
		if (num3 < 40.0 && num4 < 7.0 && t - _startT > 150)
		{
			ToScroll(count, t, scrollRows);
			return;
		}
		if (Math.Max(num, num2) >= 4.0)
		{
			double num5 = ((finger.Y > finger2.Y) ? num : num2);
			double num6 = ((finger.Y > finger2.Y) ? num2 : num);
			if (num5 < 1.5)
			{
				return;
			}
			if (num6 < 1.5)
			{
				ToScroll(count, t, scrollRows);
				return;
			}
		}
		if (!(num < 1.5) && !(num2 < 1.5))
		{
			if (SameDirections(Direction(in finger), Direction(in finger2)))
			{
				ToScroll(count, t, scrollRows);
			}
			else
			{
				ToPinch();
			}
		}
	}

	private void ToScroll(int count, long t, bool scrollRows)
	{
		State = GestureState.Scroll;
		_scrollRows = scrollRows;
		_ring.Clear();
		_windowStart = t;
		_accX = (_accY = 0.0);
		double num = 0.0;
		double num2 = 0.0;
		for (int i = 0; i < count; i++)
		{
			(double, double) tuple = _initial[_fingers[i].Id];
			num += _fingers[i].X - tuple.Item1;
			num2 += _fingers[i].Y - tuple.Item2;
		}
		num /= (double)count;
		num2 /= (double)count;
		_totalMmX = num;
		_totalMmY = num2;
		_cumX = Px(num);
		_cumY = Px(num2);
		_ring.Add((_lastUs, _cumX, _cumY));
		if (_scrollRows)
		{
			_sink.ScrollBegan(GestureId, _cumX, _cumY, t);
		}
	}

	private void ToPinch()
	{
		State = GestureState.Pinch;
		_dist0 = InitialDistance();
		MeasurePinch(2);
	}

	private void MeasurePinch(int count)
	{
		Finger finger = _fingers[0];
		Finger finger2 = _fingers[1];
		_pinchDist = Math.Sqrt((finger.X - finger2.X) * (finger.X - finger2.X) + (finger.Y - finger2.Y) * (finger.Y - finger2.Y));
		double num = 0.0;
		double num2 = 0.0;
		int num3 = 0;
		for (int i = 0; i < count; i++)
		{
			if (_initial.TryGetValue(_fingers[i].Id, out (double, double) value))
			{
				num += _fingers[i].X - value.Item1;
				num2 += _fingers[i].Y - value.Item2;
				num3++;
			}
		}
		if (num3 != 0)
		{
			double pinchDxMm = num / (double)num3;
			double pinchDyMm = num2 / (double)num3;
			_pinchDxMm = pinchDxMm;
			_pinchDyMm = pinchDyMm;
		}
		else
		{
			_pinchDxMm = 0.0;
			_pinchDyMm = 0.0;
		}
	}

	private void Accumulate(int count, long us, long t)
	{
		double num = 0.0;
		double num2 = 0.0;
		int num3 = 0;
		for (int i = 0; i < count; i++)
		{
			if (_prev.TryGetValue(_fingers[i].Id, out (double, double) value))
			{
				num += _fingers[i].X - value.Item1;
				num2 += _fingers[i].Y - value.Item2;
				num3++;
			}
		}
		if (num3 != 0)
		{
			num /= (double)num3;
			num2 /= (double)num3;
			_totalMmX += num;
			_totalMmY += num2;
			double num4 = Px(num);
			double num5 = Px(num2);
			_accX += num4;
			_accY += num5;
			_cumX += num4;
			_cumY += num5;
			_ring.Add((us, _cumX, _cumY));
			if (_ring.Count > 64)
			{
				_ring.RemoveRange(0, _ring.Count - 64);
			}
			if (t - _windowStart >= 16)
			{
				FlushWindow(t);
			}
		}
	}

	private double Px(double mm)
	{
		if (mm != 0.0)
		{
			return mm * _gain * (double)_sign;
		}
		return 0.0;
	}

	private void FlushWindow(long t)
	{
		if (_scrollRows && (_accX != 0.0 || _accY != 0.0))
		{
			_sink.ScrollChanged(GestureId, _accX, _accY, t);
		}
		_accX = (_accY = 0.0);
		_windowStart = t;
	}

	private void EndGesture(long t, ScrollEndReason reason, bool keepInitial = false)
	{
		switch (State)
		{
		case GestureState.Scroll:
			FlushWindow(_lastT);
			if (_scrollRows)
			{
				var (velocityX, velocityY) = Velocity();
				_sink.ScrollEnded(GestureId, velocityX, velocityY, _totalMmX, _totalMmY, reason, t);
			}
			else
			{
				_sink.GestureRow("pan", _gestureFingers, null, _totalMmX, _totalMmY, t - _startT, GestureId, t);
			}
			break;
		case GestureState.Pinch:
			_sink.GestureRow("pinch", _gestureFingers, (_dist0 > 0.0) ? (_pinchDist / _dist0) : 1.0, _pinchDxMm, _pinchDyMm, t - _startT, GestureId, t);
			break;
		case GestureState.Other:
			if (_gestureFingers >= 3)
			{
				var (num, num2) = MeanMove();
				if (Math.Sqrt(num * num + num2 * num2) >= 5.0)
				{
					_sink.GestureRow("swipe", _gestureFingers, null, num, num2, t - _startT, GestureId, t);
				}
				else
				{
					_sink.GestureWithoutRow();
				}
				break;
			}
			if (_gestureFingers < 2)
			{
				break;
			}
			goto case GestureState.Pending;
		case GestureState.Pending:
			_sink.GestureWithoutRow();
			break;
		}
		State = GestureState.Idle;
		if (!keepInitial)
		{
			_initial.Clear();
		}
		_ring.Clear();
		_accX = (_accY = 0.0);
		_totalMmX = (_totalMmY = 0.0);
		_cumX = (_cumY = 0.0);
		_pendingCount = 0;
	}

	private void UpdatePrev(int count)
	{
		_prev.Clear();
		for (int i = 0; i < count; i++)
		{
			_prev[_fingers[i].Id] = (_fingers[i].X, _fingers[i].Y);
		}
	}

	private double Moved(in Finger finger)
	{
		if (!_initial.TryGetValue(finger.Id, out (double, double) value))
		{
			_initial[finger.Id] = (finger.X, finger.Y);
			return 0.0;
		}
		double num = finger.X - value.Item1;
		double num2 = finger.Y - value.Item2;
		return Math.Sqrt(num * num + num2 * num2);
	}

	private int Direction(in Finger finger)
	{
		(double, double) tuple = _initial[finger.Id];
		return ((int)Math.Round(Math.Atan2(finger.Y - tuple.Item2, finger.X - tuple.Item1) / (Math.PI / 4.0)) + 8) % 8;
	}

	private static bool SameDirections(int a, int b)
	{
		int num = Math.Abs(a - b) % 8;
		return Math.Min(num, 8 - num) <= 1;
	}

	private bool IsPinch()
	{
		Finger finger = _fingers[0];
		Finger finger2 = _fingers[1];
		if (Moved(in finger) >= 1.5 && Moved(in finger2) >= 1.5)
		{
			return !SameDirections(Direction(in finger), Direction(in finger2));
		}
		return false;
	}

	private double InitialDistance()
	{
		if (_initial.Count < 2)
		{
			return 0.0;
		}
		((double X, double Y) A, (double X, double Y) B) tuple = TwoInitials();
		(double, double) item = tuple.A;
		(double, double) item2 = tuple.B;
		return Math.Sqrt((item.Item1 - item2.Item1) * (item.Item1 - item2.Item1) + (item.Item2 - item2.Item2) * (item.Item2 - item2.Item2));
	}

	private ((double X, double Y) A, (double X, double Y) B) TwoInitials()
	{
		(double, double) item = default;
		(double, double) item2 = default;
		int num = 0;
		foreach (KeyValuePair<int, (double, double)> item3 in _initial)
		{
			switch (num)
			{
			case 0:
				item = item3.Value;
				break;
			case 1:
				item2 = item3.Value;
				break;
			}
			num++;
		}
		return (A: item, B: item2);
	}

	private (double DxMm, double DyMm) MeanMove()
	{
		double num = 0.0;
		double num2 = 0.0;
		int num3 = 0;
		foreach (KeyValuePair<int, (double, double)> item in _prev)
		{
			if (_initial.TryGetValue(item.Key, out (double, double) value))
			{
				num += item.Value.Item1 - value.Item1;
				num2 += item.Value.Item2 - value.Item2;
				num3++;
			}
		}
		if (num3 != 0)
		{
			return (DxMm: num / (double)num3, DyMm: num2 / (double)num3);
		}
		return (DxMm: 0.0, DyMm: 0.0);
	}

	private (double Vx, double Vy) Velocity()
	{
		if (_ring.Count < 2)
		{
			return (Vx: 0.0, Vy: 0.0);
		}
		List<(long Us, double X, double Y)> ring = _ring;
		long item = ring[ring.Count - 1].Us;
		int num = _ring.Count - 1;
		while (num > 0 && item - _ring[num - 1].Us <= 50000)
		{
			num--;
		}
		int num2 = _ring.Count - num;
		if (num2 < 3)
		{
			List<(long Us, double X, double Y)> ring2 = _ring;
			(long, double, double) tuple = ring2[ring2.Count - 2];
			List<(long Us, double X, double Y)> ring3 = _ring;
			(long, double, double) tuple2 = ring3[ring3.Count - 1];
			double num3 = (double)(tuple2.Item1 - tuple.Item1) / 1000000.0;
			if (!(num3 <= 0.0))
			{
				return (Vx: (tuple2.Item2 - tuple.Item2) / num3, Vy: (tuple2.Item3 - tuple.Item3) / num3);
			}
			return (Vx: 0.0, Vy: 0.0);
		}
		double num4 = 0.0;
		double num5 = 0.0;
		double num6 = 0.0;
		for (int i = num; i < _ring.Count; i++)
		{
			num4 += (double)(_ring[i].Us - item) / 1000000.0;
			num5 += _ring[i].X;
			num6 += _ring[i].Y;
		}
		double num7 = num4 / (double)num2;
		double num8 = num5 / (double)num2;
		double num9 = num6 / (double)num2;
		double num10 = 0.0;
		double num11 = 0.0;
		double num12 = 0.0;
		for (int j = num; j < _ring.Count; j++)
		{
			double num13 = (double)(_ring[j].Us - item) / 1000000.0 - num7;
			num10 += num13 * num13;
			num11 += num13 * (_ring[j].X - num8);
			num12 += num13 * (_ring[j].Y - num9);
		}
		if (!(num10 <= 0.0))
		{
			return (Vx: num11 / num10, Vy: num12 / num10);
		}
		return (Vx: 0.0, Vy: 0.0);
	}
}
