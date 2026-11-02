# Windows Capabilities & System Permissions Analysis - Kuro Windows Desktop

> **Document:** `decompiled/analysis/permissions_analysis.md`  
> **Source Target:** `csharp_source/KuroWindows/app.manifest`, `InputMonitor.cs`, `SystemAudioSession.cs`, `DeviceKey.cs`

---

## 1. Process Execution Context & Privilege Level

Kuro runs as a **standard user (Medium Integrity Level)** without requiring UAC elevation (`Run as administrator`):

```xml
<!-- Extracted from app.manifest -->
<requestedExecutionLevel level="asInvoker" uiAccess="false" />
```

- **Per-User Installation:** Installs into `%LOCALAPPDATA%` and `%APPDATA%\Kuro`, ensuring smooth background execution and silent automatic updates via Velopack.
- **`uiAccess="false"`:** The application does not require UI accessibility execution rights; instead, it uses standard Win32 hooks and an isolated out-of-process helper for UIA inspections.

---

## 2. Low-Level Operating System Capabilities

| Capability | Win32 API Mechanism | Purpose in Kuro | Permission Model |
| :--- | :--- | :--- | :--- |
| **Global Mouse Hook** | `SetWindowsHookEx(WH_MOUSE_LL)` | Global cursor tracking, click counting, scroll delta recording | Standard user desktop session; no elevation required |
| **Global Keyboard Hook**| `SetWindowsHookEx(WH_KEYBOARD_LL)` | Keystroke counting, scan code capture, layout resolution | Standard user desktop session; automatically masked in password fields |
| **Window Event Hook** | `SetWinEventHook(EVENT_SYSTEM_FOREGROUND)` | Active application and window title tracking | Standard desktop hook; receives window activation events |
| **Clipboard Listener** | `AddClipboardFormatListener` | Tracks clipboard copy/paste sequence numbers without storing data | Standard user window message listener |
| **Screen Duplication** | `IDXGIOutputDuplication` / `Windows.Graphics.Capture` | High-framerate desktop display frame acquisition | Standard user desktop session; blocked on Secure Desktop |
| **Audio Loopback** | `IAudioClient` (`AUDCLNT_STREAMFLAGS_LOOPBACK`) | WASAPI system audio playback capture | Standard multimedia device access |
| **TPM 2.0 Access** | `NCryptOpenStorageProvider("Microsoft Platform Crypto Provider")` | Creates and uses non-exportable hardware private keys | Standard user CNG provider access; TPM handles hardware storage |
| **Hardware Inspection**| WMI queries (`Win32_BaseBoard`, `Win32_Processor`) | Motherboard UUID, SMBIOS serial, CPU vendor auditing | Standard user WMI read-only queries |
| **Registry Auto-Start**| `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` | Registers startup launch with `--background` flag | Per-user registry write access (`HKCU`) |
| **Crash Mini-Dumps** | `HKCU\Software\Microsoft\Windows\Windows Error Reporting\LocalDumps` | Enables automatic local WER dump creation | Per-user WER registry configuration |

---

## 3. Privacy & Elevated Window Protection

1. **UAC Secure Desktop Isolation:** When Windows switches to the elevated Secure Desktop (e.g. UAC prompts or Ctrl+Alt+Del), DirectX Desktop Duplication returns `DXGI_ERROR_ACCESS_LOST`. Kuro catches this condition (`CaptureAccessDeniedException`) and enters a hold state, emitting black frames to prevent leaking administrative credentials.
2. **Process Integrity Level Auditing (`lowerIl`):** `InputMonitor` queries the security token of the target foreground process (`OpenProcessToken` + `GetTokenInformation`). If the target has low integrity (e.g. AppContainer or sandbox), Kuro tags the event with `lowerIl: true`.
3. **Password & Credential Masking:** Keystrokes entering password inputs (`CurrentIsPassword == true`) are stripped of their character payloads and recorded only as generic timing pulses.

