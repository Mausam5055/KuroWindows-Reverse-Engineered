# Configuration, Environment & Registry Reference - Kuro Windows Desktop

> **Document:** `CONFIGURATION.md`  
> **Source Target:** `KuroWindows.AppConfig`, `UserSettings.cs`, `DesktopConfigCache.cs`, `StartupManager.cs`

---

## 1. Environment Variables Reference

Kuro supports a comprehensive suite of environment variables to override cloud endpoints, telemetry destinations, polling cadences, and diagnostic flags:

| Variable | Default Value | Min / Constraints | Description |
| :--- | :--- | :--- | :--- |
| **`KURO_SUPABASE_URL`** | `https://auth.trykuro.app` | Valid HTTPS URI | Base URL for Supabase Auth and Edge Functions |
| **`KURO_SUPABASE_PUBLISHABLE_KEY`** | `sb_publishable_2rMMms2At3Zdbmx589hloQ_wjnd4tkZ` | Non-empty string | Public anonymous gateway key for Supabase requests |
| **`KURO_UPDATE_FEED_URL`** | `https://updates.trykuro.app/win` | Valid HTTPS URI | URL for the Velopack release feed |
| **`KURO_NATIVE_AUTH_REDIRECT_URL`** | `https://trykuro.app/auth/native-callback` | Valid HTTPS URI | Fallback OAuth completion landing page |
| **`KURO_LOOPBACK_AUTH_REDIRECT_URL`**| `http://127.0.0.1:57391/auth/callback` | Valid HTTP URI | Local loopback server destination for OAuth PKCE redirect |
| **`KURO_ENABLE_CUSTOM_SCHEME_FALLBACK`** | `1` (Enabled) | Set to `0` to disable | Controls registration of `com.kuro.app://` protocol handler |
| **`KURO_CAPABILITY_PRIVACY_POLL_MS`**| `150` ms | Min: `100` ms | Interval for polling webcam and microphone registry status |
| **`KURO_CAPABILITY_POLL_MS`** | `500` ms | Min: `250` ms | Interval for general peripheral hardware capability polling |
| **`KURO_MOUSE_DELTA_FLUSH_MS`** | `8` ms | Min: `0` ms | Coalescing buffer window for high-frequency mouse coordinates |
| **`KURO_CAPTURE_DEBUG`** | Unset (`0`) | Set to `1` to enable | Enables detailed GPU texture, frame drop, and shader debug logs |
| **`KURO_SYSTEM_AUDIO`** | Unset | String identifier | Custom override for audio device selection or capture bypass |

---

## 2. Hardcoded System Constants & Timeouts (`AppConfig.cs`)

These architectural limits govern system health, hardware polling, and retry policies:

### 2.1 Video & Recording Limits
- **`DefaultCaptureFramesPerSecond = 30`**: Target display capture cadence.
- **`ClipDurationSeconds = 300`**: Nominal duration (5 minutes) of each recorded segment.
- **`MinimumDiskSpaceBytes = 2,000,000,000` (2 GB)**: Absolute floor for single-display capture.
- **Multi-Display Disk Formula:**
  $$\text{MinDiskBytes} = \max(2\,\text{GB}, (\text{DisplayCount} \times 1\,\text{GB}) + 1\,\text{GB})$$
- **`DiskCheckIntervalSeconds = 30`**: Frequency of disk space health checks via `GetDiskFreeSpaceExW`.

### 2.2 Input & UI Automation Limits
- **`WindowTitlePollMs = 500`**: Frequency of active window title evaluation.
- **`TextSelectionPollMs = 2000`**: Cadence for accessibility text selection tracking.
- **`UiaProbeBridgeDeadlineMs = 400`**: Maximum allowable timeout for out-of-process UIA helper queries.
- **`UiaProbeForegroundDeadlineMs = 750`**: Timeout for foreground window accessibility tree walk.
- **`UiaProbeConnectTimeoutMs = 10000`**: Named pipe connection timeout for helper worker startup.
- **`UiaProbeSpawnFailureLimit = 3`**: Consecutive crash limit before suppressing UIA probing on an application.
- **`UiaProbeAppSuppressionMinutes = 30`**: Duration a crash-prone third-party app is excluded from UIA inspection.

### 2.3 Network, Cloud & Upload Policies
- **`S3UploadTimeoutMinutes = 30`**: Maximum duration allowed for an S3 upload stream.
- **`BackgroundUploadRetryDelaysSeconds = [60, 180, 300]`**: Progressive retry backoffs for failed uploads.
- **`CaptureRetryDelaysSeconds = [3, 3, 3, 10, 30, 60]`**: Progressive retry backoffs for capture device reinitialization.
- **`CaptureRetrySettleSeconds = 60`**: Period required before clearing capture failure counters.

### 2.4 Cryptographic & Attestation Budgets
- **`DeviceKeyBudget = 3.0s`**: Timeout budget for TPM key lookup or generation.
- **`EndorsementCertificateBudget = 3.0s`**: Maximum time allowed to retrieve TPM EK certificates.
- **`HardwareFacetsBudget = 2.0s`**: Execution deadline for SMBIOS/WMI hardware queries.
- **`CloudMetadataProbeTimeout = 200ms`**: Connection timeout to `169.254.169.254` for VM detection.

---

## 3. Windows Registry Configuration

Kuro modifies or monitors the following registry locations:

### 3.1 Application Auto-Start
- **Registry Key:** `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`
- **Value Name:** `KuroWindows`
- **Value Data:** `"<InstallPath>\KuroWindows.exe" --background`
- Managed dynamically via [`StartupManager.SyncRegistration()`](file:///d:/Kuro%20and%20Loggy3/extracted/lib/app/KuroWindows.dll) based on user settings.

### 3.2 Custom URI Protocol Handler
- **Registry Key:** `HKCU\Software\Classes\com.kuro.app`
- **Subkey:** `shell\open\command`
- **Value Data:** `"<InstallPath>\KuroWindows.exe" --auth-callback "%1"`
- Registered on startup to enable fallback OAuth redirects.

### 3.3 Windows Error Reporting (WER) Local Mini-Dumps
- **Registry Key:** `HKCU\Software\Microsoft\Windows\Windows Error Reporting\LocalDumps\KuroWindows.exe`
- **Configured Values:**
  - `DumpFolder` (`REG_EXPAND_SZ`): `%LOCALAPPDATA%\CrashDumps`
  - `DumpType` (`REG_DWORD`): `1` (Mini-dump with basic call stack and thread memory)
  - `DumpCount` (`REG_DWORD`): `3` (Retains maximum 3 historical dumps)

### 3.4 Hardware Capability Access Store (Monitored)
- **Registry Key:** `HKCU\Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\webcam\NonPackaged`
- Evaluates `LastUsedTimeStop == 0` across active application subkeys to detect active webcam usage.

---

## 4. Local Filesystem Layout

Kuro stores all data, caches, and runtime markers in the user's `AppData\Roaming` folder:

```
%APPDATA%\Kuro\
│
├── .data\                                  (Hidden attribute enabled)
│   └── 2026-11-02_18-30-00\                (Timestamped session directory)
│       ├── .active-session.json            (Heartbeat recovery marker)
│       ├── clip_0.mp4                      (H.264 display video)
│       ├── events_0.jsonl                  (High-frequency input events)
│       ├── frame_timestamps_0.jsonl        (Frame timing & cursor metadata)
│       ├── audio.m4a                       (AAC loopback audio)
│       ├── segment_0.manifest.json         (Sealed cryptographic manifest)
│       └── metadata_0.json                 (Segment & hardware facts)
│
├── auth\
│   ├── session.dpapi                       (DPAPI fallback credential vault)
│   ├── ek-cert.der                         (Cached TPM Endorsement Certificate)
│   └── ek-cert.attempted                   (Marker preventing redundant PKI queries)
│
├── desktop_config.json                     (Offline cache of cloud feature flags & policies)
├── settings.json                           (User configuration: launch, theme, onboarding)
└── system_audio_probe.json                 (Cached audio hardware format capabilities)
```

