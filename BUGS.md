# Comprehensive Bug & Vulnerability Report: Kuro Windows Desktop

> **Target Artifact:** `KuroWindows-win-Setup.exe` (Version `1.1.19.0`, Architecture: `win-x64`)  
> **Classification:** Static Code Analysis, Race Condition Discovery & Architectural Flaws  
> **Source Base:** [`decompiled/csharp_source/`](file:///d:/Kuro%20and%20Loggy3/decompiled/csharp_source)

---

## 1. Executive Summary & Bug Severity Matrix

This document provides an exhaustive, code-level analysis of **confirmed software defects, architectural flaws, race conditions, memory/handle leaks, and silent degradation issues** discovered during the forensic decompilation of the Kuro Windows application.

```
┌────────────────────────────────────────────────────────────────────────────────────────┐
│                                  DEFECT SEVERITY MATRIX                                │
├────────┬────────────────────────────────────────────────┬──────────┬───────────────────┤
│ Bug ID │ Defect Description                             │ Severity │ Impacted Module   │
├────────┼────────────────────────────────────────────────┼──────────┼───────────────────┤
│ BUG-01 │ Fatal Session Crash on Directory Collision     │ CRITICAL │ StorageManager    │
│ BUG-02 │ Disk Space Safety Floor Bypass (Drive Fill)    │ HIGH     │ StorageManager    │
│ BUG-03 │ Fire-and-Forget Unobserved Async Task Crashes  │ HIGH     │ SessionCoordinator│
│ BUG-04 │ Win32 Message Loop Starvation on Gaming Mice   │ HIGH     │ InputMonitor      │
│ BUG-05 │ Malformed Event Line Zeroes Contributor Payout │ HIGH     │ MetadataBuilder   │
│ BUG-06 │ Incomplete UIA Password Detection & Leakage   │ HIGH     │ UiaGuard          │
│ BUG-07 │ Unhandled CoreAudio COM Exception in Loopback  │ MEDIUM   │ LoopbackCapture   │
│ BUG-08 │ Silent Exception Swallowing in Contention Loop │ MEDIUM   │ CaptureContention │
│ BUG-09 │ Cleartext Local Media Staging in AppData       │ MEDIUM   │ StorageManager    │
│ BUG-10 │ IPC Timeout Disconnect in WebView2 CoreBridge  │ LOW-MED  │ communications.js │
└────────┴────────────────────────────────────────────────┴──────────┴───────────────────┘
```

---

## 2. Detailed Bug Reports

### BUG-01: Synchronous Thread Sleep & Fatal `IOException` Crash in `CreateSessionDirectory`
* **Severity:** **CRITICAL**
* **Impacted Module:** [`StorageManager.cs`](file:///d:/Kuro%20and%20Loggy3/decompiled/csharp_source/KuroWindows/KuroWindows.Storage/StorageManager.cs#L40-L56)
* **Affected Symbol:** `KuroWindows.Storage.StorageManager.CreateSessionDirectory()`

#### Decompiled Source Code:
```csharp
public string CreateSessionDirectory()
{
    for (int i = 0; i < 3; i++)
    {
        string path = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
        string text = Path.Combine(RecordingsDirectory(), path);
        if (!Directory.Exists(text))
        {
            Directory.CreateDirectory(text);
            return text;
        }
        if (i < 2)
        {
            Thread.Sleep(1100 - DateTime.Now.Millisecond);
        }
    }
    throw new IOException("Session directory name collided repeatedly (clock step?).");
}
```

#### Root Cause Analysis:
1. **Low Timestamp Entropy:** The directory name is formatted strictly as `yyyy-MM-dd_HH-mm-ss`. It lacks millisecond precision, random entropy, or a UUID suffix.
2. **Synchronous Thread Blocking:** When a collision occurs, the code calls `Thread.Sleep(1100 - DateTime.Now.Millisecond)` synchronously on the thread coordinating session startup, causing noticeable UI stutter.
3. **Fatal Exception on Leftover Folders:** If a previous abnormal termination (crash, power loss, or force kill) left behind an existing directory matching the current second, or if an NTP synchronization steps the system clock back by a few seconds, the retry loop exhausts its 3 iterations and throws an unhandled `IOException("Session directory name collided repeatedly (clock step?).")`.
4. **Impact:** The entire recording session startup fails immediately, preventing the user from recording and earning rewards.

#### Proposed Remediation:
Append high-entropy randomness or a UUIDv7 to ensure directory creation never collides:
```csharp
public string CreateSessionDirectory()
{
    string path = $"{DateTime.UtcNow:yyyy-MM-dd_HH-mm-ss}_{Guid.NewGuid():N}";
    string text = Path.Combine(RecordingsDirectory(), path);
    Directory.CreateDirectory(text);
    return text;
}
```

---

### BUG-02: Disk Space Safety Floor Bypass (Silent Drive Exhaustion)
* **Severity:** **HIGH**
* **Impacted Module:** [`StorageManager.cs`](file:///d:/Kuro%20and%20Loggy3/decompiled/csharp_source/KuroWindows/KuroWindows.Storage/StorageManager.cs#L90-L98)
* **Affected Symbol:** `KuroWindows.Storage.StorageManager.AvailableDiskSpaceBytes()`

#### Decompiled Source Code:
```csharp
public long AvailableDiskSpaceBytes()
{
    string text = RootPath;
    while (!string.IsNullOrEmpty(text) && !Directory.Exists(text))
    {
        text = Path.GetDirectoryName(text);
    }
    if (!string.IsNullOrEmpty(text) && GetDiskFreeSpaceExW(text, out var lpFreeBytesAvailableToCaller, out var _, out var _))
    {
        if (lpFreeBytesAvailableToCaller <= long.MaxValue)
        {
            return (long)lpFreeBytesAvailableToCaller;
        }
        return long.MaxValue;
    }
    try
    {
        return new DriveInfo(Path.GetPathRoot(RootPath)).AvailableFreeSpace;
    }
    catch (Exception ex)
    {
        if (Interlocked.Exchange(ref _diskProbeFailureLogged, 1) == 0)
        {
            KuroLog.SystemEvent($"disk free-space probe failed ... treating as unlimited", ...);
        }
        return long.MaxValue; // BUG: Treats disk space as 9.2 Exabytes on probe failure
    }
}
```

#### Root Cause Analysis:
1. When `GetDiskFreeSpaceExW` and `DriveInfo` fail (common on redirected user folders, mapped network shares, virtualized volumes, or permission-restricted environments), the fallback `catch` block returns `long.MaxValue` ($9,223,372,036,854,775,807$ bytes).
2. The `SessionCoordinator` evaluates `AvailableDiskSpaceBytes() < 5_368_709_120L` (the 5 GB safety floor). Because `long.MaxValue` is returned, the check always passes.
3. **Impact:** Kuro continues capturing high-bitrate 30 FPS video until the physical hard drive reaches **0 bytes free**. This causes disk exhaustion, crashing Windows services, corrupting open databases, and freezing the host operating system.

#### Proposed Remediation:
Fail closed on probe errors by returning `0L`, halting recording safely before disk saturation:
```csharp
catch (Exception ex)
{
    KuroLog.SystemEvent($"disk probe failed: {ex.Message}; halting capture to prevent disk exhaustion", ...);
    return 0L; // Fail closed: triggers safe low-disk pause
}
```

---

### BUG-03: Fire-and-Forget Unobserved Async Task Crashes
* **Severity:** **HIGH**
* **Impacted Module:** [`SessionCoordinator.cs`](file:///d:/Kuro%20and%20Loggy3/decompiled/csharp_source/KuroWindows/KuroWindows/SessionCoordinator.cs#L395-L535)
* **Affected Symbol:** `KuroWindows.SessionCoordinator.StartSessionAdmitted()`

#### Decompiled Source Code:
```csharp
// Line 395:
Task.Run(() => HandleCaptureFailureAsync(ex3)); // BUG: Creates an unawaited Task<Task>

// Line 524 & 532:
catch (Exception ex)
{
    EmitSessionStartFailed(ex);
    HandleCaptureFailureAsync(ex); // BUG: async Task called without await in synchronous catch
    return RecoveryStartDisposition.Failed;
}
```

#### Root Cause Analysis:
1. `HandleCaptureFailureAsync` is declared as `private async Task HandleCaptureFailureAsync(Exception ex)`.
2. In lines 524 and 532, it is invoked fire-and-forget inside the synchronous `StartSessionAdmitted` method without `await`, without assigning the resulting `Task`, and without a continuation handler (`.ContinueWith(...)`).
3. In line 395, `Task.Run(() => HandleCaptureFailureAsync(ex3))` passes a method that returns a `Task` to `Task.Run(Action)`. This constructs a `Task<Task>`. Because `.Unwrap()` is never called, the outer task completes immediately while the inner task runs unmonitored.
4. **Impact:** If `HandleCaptureFailureAsync` encounters a secondary exception (e.g. database access failure, telemetry timeout, or disposal race), it produces an **unobserved task exception**, which can trigger process termination depending on .NET runtime policy.

---

### BUG-04: Win32 Message Loop Starvation on High-Polling Gaming Mice
* **Severity:** **HIGH**
* **Impacted Module:** [`InputMonitor.cs`](file:///d:/Kuro%20and%20Loggy3/decompiled/csharp_source/KuroWindows/KuroWindows.Input/InputMonitor.cs#L1723-L1760)
* **Affected Symbol:** `KuroWindows.Input.InputMonitor.MouseHookProc()` & `HandleMouseMessage()`

#### Decompiled Source Code:
```csharp
private nint MouseHookProc(int nCode, nint wParam, nint lParam)
{
    if (nCode >= 0)
    {
        try
        {
            HandleMouseMessage(wParam, lParam); // BUG: Synchronous processing on message loop
        }
        catch (Exception ex)
        {
            NoteCallbackFailure("mouse_hook", ex);
        }
    }
    return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
}
```

#### Root Cause Analysis:
1. The low-level mouse hook (`WH_MOUSE_LL`) intercepts every mouse hardware event synchronously on the thread's Win32 message queue.
2. Inside `HandleMouseMessage`, Kuro executes:
   * Native structure marshaling (`Marshal.PtrToStructure<MSLLHOOKSTRUCT>`).
   * Display boundary lookups (`MonitorTable.Current.Locate`).
   * Frame state updates (`_frameState?.UpdateMousePosition`).
   * Heap allocations (`new EventRow { ... }`).
3. Modern gaming mice report coordinates at **1,000 Hz, 4,000 Hz, or 8,000 Hz** (1,000 to 8,000 callbacks per second).
4. **Impact:** Heavy processing per mouse move causes noticeable cursor stutter in games and high-DPI applications. Furthermore, if hook execution exceeds Windows' `LowLevelHooksTimeout` (default 300 ms), **Windows silently unhooks `_mouseHook` without warning**, causing cursor tracking to stop working entirely.

#### Proposed Remediation:
Offload event payload parsing and boundary lookups to a dedicated `Channel<MSLLHOOKSTRUCT>` worker thread, keeping `MouseHookProc` under 5 microseconds.

---

### BUG-05: Malformed Event Line Zeroes Out Contributor Earnings
* **Severity:** **HIGH**
* **Impacted Module:** [`MetadataBuilder.cs`](file:///d:/Kuro%20and%20Loggy3/decompiled/csharp_source/KuroWindows/KuroWindows.Upload/MetadataBuilder.cs#L845-L848)
* **Affected Symbol:** `KuroWindows.Upload.MetadataBuilder.ScanEvents()`

#### Decompiled Source Code:
```csharp
catch (Exception ex)
{
    KuroLog.Upload($"Event scan failed for {Path.GetFileName(eventsPath)}; treating full segment duration as idle: {ex.GetType().Name}: {ex.Message}", "ScanEvents", ...);
    return EventScanResult.Empty with
    {
        DurationMs = durationMs,
        IdleMs = durationMs // BUG: Penalizes contributor with 100% idle time
    };
}
```

#### Root Cause Analysis:
1. During segment sealing, `ScanEvents` reads `events_*.jsonl` line by line to calculate human activity metrics (active keystrokes, mouse drags, scroll gestures).
2. If even a **single line** is malformed (e.g. truncated line from an unexpected power cut, or unescaped Unicode characters in an active window title), the `catch (Exception ex)` block catches the error and **sets `IdleMs = durationMs` (100% idle)**.
3. **Impact:** The user gets **0 credits / 0 reward payout** for that entire 5-minute recording segment, even though they actively interacted with the machine throughout the recording.

#### Proposed Remediation:
Implement fault-tolerant line-by-line parsing: skip corrupted lines and log a warning, rather than discarding the entire 5-minute activity history:
```csharp
// Read lines resiliently, discarding corrupt individual rows:
foreach (string line in File.ReadLines(eventsPath))
{
    try { ProcessEventRow(line); }
    catch { /* Skip malformed row, preserve valid segment history */ }
}
```

---

### BUG-06: Incomplete UI Automation Password Detection
* **Severity:** **HIGH (Security & Privacy)**
* **Impacted Module:** [`UiaGuard.cs`](file:///d:/Kuro%20and%20Loggy3/decompiled/csharp_source/KuroWindows/KuroWindows.Input.Uia/UiaGuard.cs)
* **Affected Symbol:** `KuroWindows.Input.Uia.UiaGuard.CheckFocusedElement()`

#### Root Cause Analysis:
1. Password masking relies strictly on the Windows accessibility property `UIA_IsPasswordPropertyId == true`.
2. The logic fails to detect:
   * **Terminal / CLI Windows:** PowerShell, CMD, Bash, WSL, and SSH password prompts (`sudo`, database passwords) have no UIA password properties.
   * **Custom Web Inputs:** Web login pages built with custom React, Vue, or WebGL components that lack explicit `aria-label="password"` attributes.
   * **Canvas-Rendered Forms:** Specialized banking or financial applications that render inputs via Direct2D/Skia without accessibility bridges.
3. **Impact:** Cleartext passwords are typed into `events_*.jsonl` and rendered directly onto 30 FPS video frames, which are then uploaded to Amazon S3.

---

### BUG-07: Unhandled COM Exception in Loopback Audio Capture
* **Severity:** **MEDIUM**
* **Impacted Module:** [`LoopbackCapture.cs`](file:///d:/Kuro%20and%20Loggy3/decompiled/csharp_source/SystemAudio/SystemAudio/LoopbackCapture.cs#L185-L195)
* **Affected Symbol:** `SystemAudio.LoopbackCapture.CaptureLoop()`

#### Decompiled Source Code:
```csharp
if (num != 0)
{
    throw new CoreAudioException("WaitForMultipleObjects", -2147467259); // E_FAIL
}
```

#### Root Cause Analysis:
1. In `LoopbackCapture.cs`, the capture loop uses `WaitForMultipleObjects` to synchronize with audio buffer ready events.
2. If the user disconnects an audio output device (e.g. unplugging a USB headset, switching Bluetooth headphones, or putting the PC to sleep), `WaitForMultipleObjects` returns `WAIT_FAILED` or an unexpected index.
3. The code immediately throws `CoreAudioException("WaitForMultipleObjects", -2147467259)`, which aborts the audio capture loop and flags the audio stream as permanently failed instead of initiating an audio endpoint renegotiation.
4. **Impact:** All subsequent video segments have silent or missing audio until Kuro is restarted.

---

### BUG-08: Silent Exception Swallowing in Contention Scanner
* **Severity:** **MEDIUM**
* **Impacted Module:** [`CaptureContentionMonitor.cs`](file:///d:/Kuro%20and%20Loggy3/decompiled/csharp_source/KuroWindows/KuroWindows.Input/CaptureContentionMonitor.cs#L455-L485)
* **Affected Symbol:** `KuroWindows.Input.CaptureContentionMonitor.TryScan()`

#### Decompiled Source Code:
```csharp
foreach (Process process in array)
{
    try
    {
        if (match == null && process.Id != _selfPid && InThisSession(process))
        {
            match = MatchProcess(process, digests, scan);
        }
    }
    catch { } // Empty catch block swallows all errors silently
    finally { process.Dispose(); }
}
```

#### Root Cause Analysis:
1. Every 5 seconds, Kuro enumerates all running processes on the system to check for competing screen capture tools (OBS, ShadowPlay, Bandicam).
2. The loop surrounds every process inspection with empty `catch { }` blocks.
3. When encountering anti-cheat protected processes (e.g., Easy Anti-Cheat, BattlEye, Riot Vanguard), calls to query process image paths throw `Win32Exception (Access Denied)`. The loop swallows these errors without diagnostics, which can cause contention scans to stall or miss active recording tools operating under elevated permissions.

---

### BUG-09: Cleartext Staging of Sensitive Media on Local Drive
* **Severity:** **MEDIUM (Security)**
* **Impacted Module:** [`StorageManager.cs`](file:///d:/Kuro%20and%20Loggy3/decompiled/csharp_source/KuroWindows/KuroWindows.Storage/StorageManager.cs#L24-L34)

#### Root Cause Analysis:
1. Video files (`clip_0.mp4`), audio files (`audio.aac`), and input logs (`events_0.jsonl`) are staged in `%APPDATA%\Kuro\.data\`.
2. The folder is flagged with `FileAttributes.Hidden`, but **zero cryptographic encryption at rest (no DPAPI, no AES-256)** is applied.
3. **Impact:** Any unprivileged application or script running under the user's account can read the folder and steal recorded screen video and keyboard logs before Kuro uploads and purges them.

---

### BUG-10: JSON-RPC Bridge Timeout Disconnect in WebView2
* **Severity:** **LOW-MEDIUM**
* **Impacted Module:** [`communications.js`](file:///d:/Kuro%20and%20Loggy3/decompiled/ui/communications.js) & `app.js`

#### Root Cause Analysis:
1. The frontend invokes backend operations via `sendCommand(method, params)` wrapped in a 10-second promise timeout.
2. If the host application is under heavy load (such as rotating a 4K 60FPS video segment or sealing an audio file), the host response may exceed 10 seconds.
3. The frontend promise rejects with a timeout error, putting the UI into an unrecoverable disconnected error state even though the backend operation succeeded.
