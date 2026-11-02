namespace KuroWindows.Input;

internal sealed record TouchpadFactsSnapshot(int Devices, long Frames, long Reports, long Partial, long Timeouts, long DupIds, long UnknownReportId, long UnknownDevice, long LenMismatch, long RawFail, long BufferGrow, long ContactsTruncated, long GestureScroll, long GesturePinch, long GestureSwipe, long GesturePan, long GestureOther, long ScrollRows, long OsRows, long OrphanWheels, long PalmContacts, bool Registered, int RegisterFail, int Reregisters);
