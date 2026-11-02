namespace KuroWindows.Input.Uia;

internal readonly record struct UiaProbeRequest(int Seq, UiaProbeSite Site, UiaProbeReads Reads, string TargetExe, string TargetClass, int TargetPid, long TargetWindow);
