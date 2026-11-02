using System;

namespace KuroWindows.Input.Touchpad;

internal sealed class PtpFrame
{
	public const int MaxContacts = 16;

	public readonly PtpContact[] Contacts = new PtpContact[16];

	public int Count;

	public int ContactCount;

	public int Buttons;

	public int ScanTimeRaw;

	public long ScanUs;

	public long Qpc;

	public long WallMs;

	public uint OsTime;

	public int Reports;

	public bool Partial;

	public bool Synthetic;

	public bool Transition;

	public bool AnyPalm;

	public int Fingers;

	public void CopyFrom(PtpFrame other)
	{
		Array.Copy(other.Contacts, Contacts, other.Count);
		Count = other.Count;
		ContactCount = other.ContactCount;
		Buttons = other.Buttons;
		ScanTimeRaw = other.ScanTimeRaw;
		ScanUs = other.ScanUs;
		Qpc = other.Qpc;
		WallMs = other.WallMs;
		OsTime = other.OsTime;
		Reports = other.Reports;
		Partial = other.Partial;
		Synthetic = other.Synthetic;
		Transition = other.Transition;
		AnyPalm = other.AnyPalm;
		Fingers = other.Fingers;
	}
}
