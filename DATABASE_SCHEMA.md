# Data Storage & Schema Reference - Kuro Windows Desktop

> **Document:** `DATABASE_SCHEMA.md`  
> **Source Target:** `KuroWindows.Input/RowSerializer.cs`, `KuroWindows.Capture/FrameSidecarWriter.cs`, `KuroWindows.RecorderCore.Segments/`, `KuroWindows.Upload/MetadataBuilder.cs`, `KuroWindows.Storage/StorageManager.cs`

---

## 1. Storage Architecture Overview

Kuro does not employ a traditional relational SQL database on the client machine. To maximize throughput during 30 FPS video recording and millisecond-accurate input streaming, it uses an **append-only immutable file storage pattern**:
1. High-frequency inputs and sidecars write to append-only **JSON Lines (`.jsonl`)** files.
2. Compressed video and audio stream to native **MP4/M4A media containers**.
3. Segment boundaries freeze artifacts into immutable directories sealed with **JSON manifests and SHA-256 digests**.
4. Ephemeral session state and cryptographic credentials write to **Windows Credential Manager** backed by **DPAPI** encrypted fallbacks.

```
%APPDATA%\Kuro\
├── .data\                                 (Hidden Storage Root)
│   └── 2026-11-02_18-30-00\               (Session Folder)
│       ├── .active-session.json           (Crash recovery heartbeat)
│       ├── clip_0.mp4                     (H.264 Video Stream)
│       ├── events_0.jsonl                 (Raw Input Events Stream)
│       ├── frame_timestamps_0.jsonl       (Frame Timing & Cursor Sidecar)
│       ├── audio.m4a                      (AAC Encoded Audio)
│       ├── segment_0.manifest.json        (Cryptographic Manifest)
│       └── metadata_0.json                (Device & Session Facts)
├── auth\
│   └── session.dpapi                      (DPAPI Encrypted Vault Fallback)
├── desktop_config.json                    (Cloud Policy & Flag Cache)
└── settings.json                          (User Preferences)
```

---

## 2. Input Event Record Schema (`events_<display>.jsonl`)

Every keyboard tap, mouse movement, click, scroll wheel notch, and touchpad contact is serialized into a line-delimited JSON record by [`RowSerializer.cs`](file:///d:/Kuro%20and%20Loggy3/extracted/lib/app/KuroWindows.dll#L10).

```json
{
  "timestamp": 1798904561234,
  "displayIndex": 0,
  "channel": "mouse",
  "x": 1920.5,
  "y": 1080.0,
  "dx": 12.0,
  "dy": -4.0,
  "button": "left",
  "isDown": true,
  "clickCount": 1,
  "pressure": 1.0,
  "deltaX": 0.0,
  "deltaY": 120.0,
  "phase": "began",
  "momentumPhase": "none",
  "isPrecise": false,
  "fromScreen": 0,
  "toScreen": 0,
  "packets": 1,
  "modifiers": 8,
  "keyCode": 65,
  "scanCode": 30,
  "extended": false,
  "characters": "a",
  "isRepeat": false,
  "dead": false,
  "hkl": "00000409",
  "extraInfo": 0,
  "lowerIl": false,
  "appName": "Visual Studio Code",
  "bundleId": "Microsoft.VSCode",
  "targetExe": "Code.exe",
  "targetPid": 14280,
  "hwnd": 197420,
  "gestureId": 0,
  "velocityX": 0.0,
  "velocityY": 0.0,
  "totalMmX": 0.0,
  "totalMmY": 0.0,
  "endReason": "normal",
  "device": "touchpad_elan",
  "scanTime": 142095,
  "contactCount": 2
}
```

### Complete Field Dictionary

| Field | Type | Description |
| :--- | :--- | :--- |
| `timestamp` | `long` | Nanosecond or millisecond recording clock timestamp |
| `displayIndex`| `int` | Monitor index where event occurred (`0`, `1`, etc.) |
| `channel` | `string` | Event classification: `mouse`, `keyboard`, `touchpad`, `window`, `clipboard` |
| `x`, `y` | `double` | Virtual desktop or monitor pixel coordinates |
| `dx`, `dy` | `double` | Sub-pixel relative displacement vector |
| `button` | `string` | Mouse button: `left`, `right`, `middle`, `x1`, `x2`, `none` |
| `isDown` | `boolean`| Button or key state (`true` for down, `false` for up) |
| `clickCount` | `int` | Single, double, or triple click counter |
| `pressure` | `double` | Normalized contact pressure (`0.0` to `1.0`) |
| `deltaX`, `deltaY` | `double` | Scroll wheel increments (horizontal and vertical) |
| `phase` | `string` | Touch/Scroll phase: `began`, `moved`, `stationary`, `ended`, `cancelled` |
| `momentumPhase`| `string`| Inertial scroll phase: `none`, `began`, `continuous`, `ended` |
| `isPrecise` | `boolean`| True for high-resolution Precision Touchpad scrolling; false for notched wheels |
| `modifiers` | `uint` | Bitmask of active modifier keys (`Shift`, `Ctrl`, `Alt`, `Win`) |
| `keyCode` | `uint` | Win32 Virtual Key code (`VK_A`, `VK_RETURN`, etc.) |
| `scanCode` | `uint` | Physical hardware keyboard scan code |
| `extended` | `boolean`| True if key is an extended keyboard key (e.g. right Alt/Ctrl) |
| `characters` | `string` | Layout-aware Unicode character representation |
| `isRepeat` | `boolean`| True if keystroke is an auto-repeat event |
| `hkl` | `string` | Active keyboard layout hex handle (e.g. `00000409` for US English) |
| `lowerIl` | `boolean`| True if target window has an Integrity Level lower than Medium |
| `appName` | `string` | Friendly active application name |
| `bundleId` | `string` | UWP AppUserModelId or bundle identifier |
| `targetExe` | `string` | Executable filename of active foreground process (e.g. `chrome.exe`) |
| `targetPid` | `int` | Process ID of target window |
| `hwnd` | `long` | Win32 window handle (`HWND`) |
| `gestureId` | `int` | Precision Touchpad gesture tracking ID |
| `contactCount`| `int` | Number of simultaneous physical fingers on touchpad |

---

## 3. Frame Sidecar Record Schema (`frame_timestamps_<display>.jsonl`)

Emitted once for **every video frame** written to `clip_<display>.mp4` by [`FrameSidecarWriter.cs`](file:///d:/Kuro%20and%20Loggy3/extracted/lib/app/KuroWindows.dll#L15) (`SidecarSchema = 2`):

```json
{
  "sidecarSchema": 2,
  "frame": 1842,
  "wallMs": 1798904561234,
  "ptsNs": 61400000000,
  "durationNs": 33333333,
  "frameKind": "dxgi_dda",
  "timestampKind": "qpc_monotonic",
  "timestampQuality": "nominal",
  "framesDroppedSincePrev": 0,
  "cursorBlendChanged": false,
  "mouse": {
    "x": 840.0,
    "y": 420.0,
    "screen": 0,
    "buttons": { "left": false, "right": false, "middle": false },
    "visible": true,
    "suppressed": false,
    "shape": "arrow"
  },
  "keysDown": [16, 65],
  "modifiers": 4,
  "activeApp": {
    "appName": "Google Chrome",
    "bundleId": "Chrome",
    "exe": "chrome.exe",
    "pid": 9824,
    "windowTitle": "GitHub - Repository Dashboard"
  },
  "scrollThisFrame": null,
  "clicksThisFrame": [
    { "button": "left", "x": 840.0, "y": 420.0, "clickCount": 1 }
  ],
  "keyTransitionsThisFrame": [
    { "keyCode": 65, "isDown": true }
  ],
  "state": {
    "isPrivacyPaused": false,
    "isContended": false,
    "isCameraActive": false
  }
}
```

---

## 4. Segment Manifest Schema (`segment_<index>.manifest.json`)

Created upon segment sealing by [`SegmentManifestStore.cs`](file:///d:/Kuro%20and%20Loggy3/extracted/lib/app/KuroWindows.dll):

```json
{
  "segmentIndex": 0,
  "sessionDir": "2026-11-02_18-30-00",
  "createdAtUtc": "2026-11-02T18:30:00.124Z",
  "closedAtUtc": "2026-11-02T18:35:00.150Z",
  "fate": "eligible_for_upload",
  "displays": [
    {
      "displayIndex": 0,
      "videoFile": "clip_0.mp4",
      "videoBytes": 154829100,
      "videoSha256": "4a7c1b8e9f2d3c4b5a6e7f8a9b0c1d2e3f4a5b6c7d8e9f0a1b2c3d4e5f6a7b8c",
      "eventsFile": "events_0.jsonl",
      "eventsBytes": 142010,
      "eventsSha256": "8f1b2c3d4e5f6a7b8c9d0e1f2a3b4c5d6e7f8a9b0c1d2e3f4a5b6c7d8e9f0a1b",
      "sidecarFile": "frame_timestamps_0.jsonl",
      "sidecarBytes": 98400,
      "sidecarSha256": "2e9a0f1b2c3d4e5f6a7b8c9d0e1f2a3b4c5d6e7f8a9b0c1d2e3f4a5b6c7d8e9f",
      "frameCount": 9000,
      "width": 1920,
      "height": 1080,
      "fps": 30.0
    }
  ],
  "audio": {
    "audioFile": "audio.m4a",
    "audioBytes": 4718200,
    "audioSha256": "3d5f6a7b8c9d0e1f2a3b4c5d6e7f8a9b0c1d2e3f4a5b6c7d8e9f0a1b2c3d4e5f",
    "durationMs": 300000,
    "sampleRate": 48000,
    "channels": 2,
    "codec": "aac_lc"
  }
}
```

---

## 5. Segment Metadata Schema (`metadata_<index>.json`)

Aggregated by [`MetadataBuilder.cs`](file:///d:/Kuro%20and%20Loggy3/extracted/lib/app/KuroWindows.dll) to provide cloud verification engines with complete forensic telemetry:

```json
{
  "version": 2,
  "session": {
    "sessionId": "2026-11-02_18-30-00",
    "userId": "usr_94a8c3d1-0f2b-4e8a-a62e-1b8f4c2e7d9a",
    "deviceId": "dev_7c9f8a2b-3e4d-5c6a-8b9e-0f1a2b3c4d5e",
    "appVersion": "1.1.19.0",
    "osVersion": "Microsoft Windows 11 Pro 10.0.22631",
    "platform": "windows_x64"
  },
  "device": {
    "cpu": "AMD Ryzen 9 7950X 16-Core Processor",
    "gpu": "NVIDIA GeForce RTX 4090",
    "ramBytes": 68719476736,
    "displayCount": 1,
    "motherboardUuid": "550e8400-e29b-41d4-a716-446655440000",
    "tpmEnrolled": true,
    "tpmManufacturer": "AMD"
  },
  "segment": {
    "segmentIndex": 0,
    "startTimeUtc": "2026-11-02T18:30:00.124Z",
    "endTimeUtc": "2026-11-02T18:35:00.150Z",
    "wallDurationMs": 300026
  },
  "activity": {
    "totalInputEvents": 4829,
    "totalKeystrokes": 1240,
    "totalMouseClicks": 312,
    "totalScrollEvents": 850,
    "mouseTraversedDistancePx": 45892.4,
    "activeAppDurations": {
      "Code.exe": 182000,
      "chrome.exe": 95000,
      "explorer.exe": 23026
    }
  },
  "mask": {
    "maskingTriggerCount": 0,
    "maskedDurationMs": 0,
    "contentionDetected": false
  },
  "integrity": {
    "timingQuality": "nominal",
    "clockJumpsDetected": 0,
    "deviceProofSignature": "MEQCICv+..."
  }
}
```

---

## 6. Secure Credential Vault (`CredentialStore.cs`)

Authentication tokens are never stored in plaintext JSON. Kuro utilizes **Windows Credential Manager** via Win32 `advapi32.dll` APIs (`CredWriteW` / `CredReadW`):

| Property | Value | Notes |
| :--- | :--- | :--- |
| **`TargetName`** | `com.kuro.app.auth` | Primary lookup target identifier |
| **`UserName`** | `kuro.auth.session` | Account identity anchor |
| **`Type`** | `CRED_TYPE_GENERIC (1)` | Generic credential record |
| **`Persist`** | `CRED_PERSIST_LOCAL_MACHINE (2)` | Persists across reboot and session sign-off |
| **Payload** | JSON Encoded String | Contains `access_token`, `refresh_token`, `user_id`, `expires_at` |

### DPAPI Fallback (`session.dpapi`)
If Windows Credential Manager is blocked by Group Policy or sandboxing:
- Path: `%APPDATA%\Kuro\auth\session.dpapi`
- Encryption: Windows Data Protection API (`crypt32.dll` `CryptProtectData` with `CRYPTPROTECT_UI_FORBIDDEN`).
- Bound strictly to the local Windows user profile security principal.

---

## 7. Configuration Caches (`desktop_config.json` & `settings.json`)

### 7.1 User Settings (`settings.json`)
Located in `%APPDATA%\Kuro\settings.json`:
```json
{
  "recording_enabled": true,
  "launch_at_startup": true,
  "theme": "dark",
  "onboarding_completed": true,
  "custom_save_path": null,
  "pending_consents": [
    {
      "user_id": "usr_94a8c3d1...",
      "type": "tos",
      "version": "2026-03-28",
      "age_confirmed": true,
      "created_at_utc": "2026-11-02T18:31:00Z"
    }
  ]
}
```

### 7.2 Cached Desktop Config (`desktop_config.json`)
Seeds the application on boot before the first cloud sync completes:
```json
{
  "config_version": 142,
  "cached_at": "2026-11-02T18:30:00Z",
  "config": {
    "video": {
      "bitrate_kbps": 2500,
      "fps": 30
    },
    "feature_flags": {
      "enable_gen2_capture": true,
      "system_audio": true,
      "hardware_facets": true
    },
    "masking": {
      "blocked_apps": ["keepass.exe", "1password.exe", "bitwarden.exe", "lastpass.exe"],
      "sensitive_browser_titles": ["bank", "paypal", "health", "credit card"]
    },
    "upload": {
      "retention_days": 1,
      "multipart_chunk_size_mb": 10
    }
  }
}
```

