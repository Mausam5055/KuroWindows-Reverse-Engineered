using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using KuroWindows.RecorderCore.Clock;

namespace KuroWindows.Input.Touchpad;

internal sealed class PtpFrameAssembler
{
	private const int ConfidenceDecisionFrames = 200;

	private const long ConfidenceDecisionMs = 5000L;

	private const long ScanTimeEpochMs = 1000L;

	private readonly PtpDevicePlan _plan;

	private readonly PtpCounters _counters;

	private readonly Action<PtpFrame> _onFrame;

	private readonly PtpFrame _frame = new PtpFrame();

	private bool _open;

	private int _expected;

	private int _received;

	private int _openScanTime;

	private int _prevRaw = -1;

	private long _scanUs;

	private long _lastReportQpc;

	private long _firstReportQpc;

	private readonly Dictionary<int, PtpContact> _prevContacts = new Dictionary<int, PtpContact>();

	private int _prevCount;

	private int _prevButtons;

	private int _contactFrames;

	private long _firstContactQpc;

	public bool? ConfidenceUsable { get; private set; }

	public bool ConfidenceDecided { get; set; }

	public bool ContactsDown { get; private set; }

	public long LastReportQpc => _lastReportQpc;

	public PtpFrameAssembler(PtpDevicePlan plan, PtpCounters counters, Action<PtpFrame> onFrame)
	{
		_plan = plan;
		_counters = counters;
		_onFrame = onFrame;
		ConfidenceUsable = (plan.HasConfidence ? ((bool?)null) : new bool?(false));
	}

	public void OnReport(PtpSlot[] slots, int slotCount, int contactCount, int scanTime, int buttons, long qpc, uint osTime)
	{
		if (_firstReportQpc == 0L)
		{
			_firstReportQpc = qpc;
		}
		long us = UnwrapScanTime(scanTime, qpc);
		_lastReportQpc = qpc;
		if (contactCount > 0)
		{
			if (_open)
			{
				Finish(partial: true);
			}
			Open(contactCount, scanTime, buttons, qpc, osTime, us);
			Take(slots, slotCount);
			if (_received >= _expected)
			{
				Finish(partial: false);
			}
		}
		else if (_open && scanTime == _openScanTime)
		{
			_frame.Reports++;
			_frame.OsTime = osTime;
			Take(slots, slotCount);
			if (_received >= _expected)
			{
				Finish(partial: false);
			}
		}
		else
		{
			if (_open)
			{
				Finish(partial: true);
			}
			Open(0, scanTime, buttons, qpc, osTime, us);
			Finish(partial: false);
		}
	}

	public void Timeout(long qpc, uint osTime)
	{
		if (_open)
		{
			Finish(partial: true);
		}
		if (ContactsDown)
		{
			Interlocked.Increment(ref _counters.Timeouts);
			Open(0, _prevRaw, 0, qpc, osTime, UnwrapScanTime(-1, qpc));
			_frame.Synthetic = true;
			Finish(partial: false);
		}
	}

	private void Open(int expected, int scanTime, int buttons, long qpc, uint osTime, long us)
	{
		_open = true;
		_expected = expected;
		_received = 0;
		_openScanTime = scanTime;
		_frame.Count = 0;
		_frame.ContactCount = expected;
		_frame.Buttons = buttons;
		_frame.ScanTimeRaw = scanTime;
		_frame.ScanUs = us;
		_frame.Qpc = qpc;
		_frame.WallMs = RecordingClock.Shared.WallMsFromQpc(qpc);
		_frame.OsTime = osTime;
		_frame.Reports = 1;
		_frame.Partial = false;
		_frame.Synthetic = false;
	}

	private void Take(PtpSlot[] slots, int slotCount)
	{
		for (int i = 0; i < slotCount; i++)
		{
			if (_received >= _expected)
			{
				break;
			}
			_received++;
			PtpSlot ptpSlot = slots[i];
			bool flag = false;
			for (int j = 0; j < _frame.Count; j++)
			{
				if (_frame.Contacts[j].Id == ptpSlot.Id)
				{
					flag = true;
					break;
				}
			}
			if (flag)
			{
				Interlocked.Increment(ref _counters.DupIds);
			}
			else if (_frame.Count < 16)
			{
				_frame.Contacts[_frame.Count++] = new PtpContact
				{
					Id = ptpSlot.Id,
					X = ptpSlot.X,
					Y = ptpSlot.Y,
					Tip = ptpSlot.Tip,
					Conf = ptpSlot.Conf
				};
			}
		}
	}

	private void Finish(bool partial)
	{
		_open = false;
		_frame.Partial = partial;
		if (partial)
		{
			Interlocked.Increment(ref _counters.Partial);
		}
		Interlocked.Increment(ref _counters.Frames);
		if (!ConfidenceUsable.HasValue && _frame.Count > 0)
		{
			if (_contactFrames++ == 0)
			{
				_firstContactQpc = _frame.Qpc;
			}
			bool flag = false;
			for (int i = 0; i < _frame.Count; i++)
			{
				flag |= _frame.Contacts[i].Conf;
			}
			if (flag)
			{
				ConfidenceUsable = true;
				ConfidenceDecided = true;
			}
			else if (_contactFrames >= 200 || RecordingClock.MsBetween(_firstContactQpc, _frame.Qpc) >= 5000)
			{
				ConfidenceUsable = false;
				ConfidenceDecided = true;
			}
		}
		bool valueOrDefault = ConfidenceUsable == true;
		bool flag2 = _frame.Count != _prevCount || _frame.ContactCount != _prevCount || _frame.Buttons != _prevButtons;
		bool flag3 = false;
		int num = 0;
		bool contactsDown = false;
		for (int j = 0; j < _frame.Count; j++)
		{
			ref PtpContact reference = ref _frame.Contacts[j];
			bool flag4 = _prevContacts.TryGetValue(reference.Id, out var value);
			bool flag5 = flag4 && value.Palm;
			reference.Palm = flag5 || (valueOrDefault && !reference.Conf);
			if (reference.Palm && !flag5)
			{
				Interlocked.Increment(ref _counters.PalmContacts);
			}
			flag3 |= reference.Palm;
			if (reference.Tip)
			{
				contactsDown = true;
				if (!reference.Palm)
				{
					num++;
				}
			}
			flag2 = !flag4 || (flag2 | (value.Tip != reference.Tip || value.Conf != reference.Conf));
		}
		_frame.Transition = flag2;
		_frame.AnyPalm = flag3;
		_frame.Fingers = num;
		ContactsDown = contactsDown;
		_onFrame(_frame);
		_prevContacts.Clear();
		for (int k = 0; k < _frame.Count; k++)
		{
			PtpContact value2 = _frame.Contacts[k];
			if (value2.Tip)
			{
				_prevContacts[value2.Id] = value2;
			}
		}
		_prevCount = _frame.Count;
		_prevButtons = _frame.Buttons;
	}

	private long UnwrapScanTime(int raw, long qpc)
	{
		if (!_plan.HasScanTime || raw < 0)
		{
			return ElapsedUs(qpc);
		}
		if (_prevRaw < 0 || RecordingClock.MsBetween(_lastReportQpc, qpc) > 1000)
		{
			_scanUs = ElapsedUs(qpc);
		}
		else
		{
			int num = raw - _prevRaw;
			if (num < 0)
			{
				num += _plan.ScanTimeModulus + 1;
			}
			_scanUs += (long)num * 100L;
		}
		_prevRaw = raw;
		return _scanUs;
	}

	private long ElapsedUs(long qpc)
	{
		long num = qpc - _firstReportQpc;
		return num / Stopwatch.Frequency * 1000000 + num % Stopwatch.Frequency * 1000000 / Stopwatch.Frequency;
	}
}
