# API & IPC Interface Reference - Kuro Windows Desktop

> **Document:** `API_REFERENCE.md`  
> **Source Target:** `KuroWindows.CoreBridge/`, `KuroWindows.UI/ShellWindow.xaml.cs`, `KuroWindows.Auth/APIClient.cs`, `KuroWindows.Dashboard/DashboardApiClient.cs`

---

## 1. Overview of Interfaces

Kuro exposes three primary communication tiers:
1. **CoreBridge IPC:** Bi-directional JSON-RPC over WebView2 message ports between the HTML/JS frontend and the WPF .NET host.
2. **ShellWindow Host Commands:** Direct Win32 window management commands intercepted by `ShellWindow.xaml.cs`.
3. **Cloud REST & Edge Function APIs:** Authenticated, device-attested HTTPS endpoints hosted on Supabase, AWS API Gateway, and Hardware TPM PKI authorities.

---

## 2. CoreBridge IPC Commands (Frontend → Backend)

All commands are posted from JavaScript using `window.chrome.webview.postMessage({ id, method, params })`. Responses follow the standard envelope:
```json
{
  "id": 12,
  "ok": true,
  "result": { ... },
  "error": null
}
```
If `ok: false`, the `error` object contains `{ "code": "string", "message": "string" }`.

### 2.1 Session & Recording Controls

| Method | Parameters | Returns | Description |
| :--- | :--- | :--- | :--- |
| `get_snapshot` | None | `CoreBridgeSnapshot` | Returns current aggregate system snapshot |
| `start_recording` | None | `CoreBridgeSnapshot` | Sets `RecordingEnabled = true`, arms capture contention, starts capture |
| `stop_recording` | None | `CoreBridgeSnapshot` | Sets `RecordingEnabled = false`, stops capture, seals active segment |
| `complete_onboarding` | None | `CoreBridgeSnapshot` | Records TOS/Privacy consent (`ConsentRecorder`), arms and starts recording |

### 2.2 Authentication & User Lifecycle

| Method | Parameters | Returns | Description |
| :--- | :--- | :--- | :--- |
| `sign_in` | None | `CoreBridgeSnapshot` | Initiates Google OAuth PKCE flow; launches system browser |
| `cancel_sign_in` | None | `CoreBridgeSnapshot` | Cancels in-flight OAuth callback server and resets auth state |
| `sign_out` | None | `CoreBridgeSnapshot` | Flushes telemetry, wipes stored session from WinCred/DPAPI, resets state |

### 2.3 Network, Cloud & Upload Operations

| Method | Parameters | Returns | Description |
| :--- | :--- | :--- | :--- |
| `retry_uploads` | None | `CoreBridgeSnapshot` | Triggers immediate sweep and retry of stalled or queued uploads |
| `request_sync` | None | `CoreBridgeSnapshot` | Forces immediate sync of remote feature flags and masking policies |
| `check_updates` | None | `CoreBridgeSnapshot` | Checks Velopack update feed for new versions |
| `install_update` | None | `CoreBridgeSnapshot` | Applies staged Velopack update and prepares for restart |

### 2.4 User Settings & Application Lifecycle

| Method | Parameters | Returns | Description |
| :--- | :--- | :--- | :--- |
| `set_launch_at_startup`| `{ "enabled": boolean }` | `CoreBridgeSnapshot` | Updates registry `HKCU\...\Run` startup configuration |
| `set_theme` | `{ "theme": "light" \| "dark" }` | `CoreBridgeSnapshot` | Persists user theme preference |
| `quit` | None | `CoreBridgeSnapshot` | Executes graceful application shutdown |

### 2.5 Dashboard & Rewards RPC (`desktop-dashboard`)

| Method | Parameters | Returns | Description |
| :--- | :--- | :--- | :--- |
| `get_dashboard` | `{ "time_zone": string }` | `DashboardData` | User stats, recorded hours today, earnings rates, pending notices |
| `get_communications`| `{ "force": boolean }` | `CommunicationsData`| Bulletin announcements, active service notices, guard warnings |
| `get_referral` | None | `ReferralData` | Referral code, custom share URL, and total invited contributors |
| `list_days` | `{ "offset": int, "time_zone": string }` | `DayHistory` | Daily historical telemetry breakdown (page size: 15) |
| `list_transactions`| `{ "offset": int, "time_zone": string }` | `TransactionHistory`| Payout and credit transaction records (page size: 15) |
| `request_payout` | `{ "method": string, "destination": string }` | `PayoutResult` | Requests reward withdrawal to specified destination (e.g. PayPal) |
| `dismiss_notice` | `{ "id": string }` | `{ "ok": true }` | Dismisses a non-critical bulletin announcement |
| `present_guard_notice`| `{ "notice_id": string, "content_revision": string }` | `{ "accepted": true }` | Legal receipt confirming user has viewed a critical guard notice |

---

## 3. ShellWindow Direct Commands (Frontend → Window Host)

These commands are intercepted directly by [`ShellWindow.xaml.cs`](file:///d:/Kuro%20and%20Loggy3/extracted/lib/app/KuroWindows.dll#L725) before reaching `CoreBridgeServer`:

| Command | Parameters | Native Implementation |
| :--- | :--- | :--- |
| `hide_window` | None | Invokes `ShellWindow.Hide()`; collapses application to system tray |
| `begin_window_drag`| None | Issues Win32 `SendMessage(hwnd, WM_NCLBUTTONDOWN, HT_CAPTION, 0)` for smooth window dragging |
| `resize_window` | `{ "width": double, "height": double }` | Adjusts `Width` and `Height`; clamps within display work area |
| `open_external_url`| `{ "url": string }` | Validates `https://` protocol and domain; launches default OS browser |

---

## 4. CoreBridge Snapshot Schema (Backend → Frontend Broadcast)

The backend broadcasts `core_bridge_snapshot` events upon any state mutation:

```json
{
  "app": {
    "name": "Kuro",
    "version": "1.1.19",
    "platform": "windows"
  },
  "auth": {
    "state": "SignedIn",
    "signed_in": true,
    "user_id": "usr_94a8c3d1-0f2b-4e8a-a62e-...",
    "email": "contributor@example.com",
    "linked_email": null,
    "ban_reason": null,
    "last_error": null
  },
  "device": {
    "id": "dev_7c9f8a2b-3e4d-5c6a-..."
  },
  "desktop_sync": {
    "status": "synced",
    "has_config": true,
    "config_version": 142,
    "synced_at": "2026-11-02T18:30:00Z",
    "next_sync_after_seconds": 3600
  },
  "recording": {
    "state": "Recording",
    "transition": null,
    "expected": true,
    "manual_controls_allowed": true,
    "current_segment_index": 3,
    "has_active_session": true
  },
  "masking": {
    "status": "normal",
    "context_type": null,
    "mode": "default",
    "blocked_app_count": 48,
    "capture_contention": false
  },
  "upload": {
    "is_uploading": true,
    "total_segments": 4,
    "completed_segments": 3,
    "total_bytes": 419430400,
    "uploaded_bytes": 314572800
  },
  "disk": {
    "available_bytes": 85899345920,
    "minimum_required_bytes": 2000000000,
    "low_disk": false
  },
  "update": {
    "status": "idle",
    "required": false,
    "available_version": null
  },
  "settings": {
    "launch_at_startup": true,
    "launch_at_startup_registered": true,
    "onboarding_completed": true,
    "theme": "dark"
  }
}
```

---

## 5. Cloud Supabase Edge Functions Reference

Base URL: `https://auth.trykuro.app/functions/v1/`  
Standard Headers:
- `Authorization: Bearer <SupabaseToken>`
- `apikey: sb_publishable_2rMMms2At3Zdbmx589hloQ_wjnd4tkZ`
- `x-device-key-id: <TPM_Key_Identifier>`
- `x-device-ts: <ISO8601_Timestamp>`
- `x-device-signature: <ECDSA_SHA256_Signature>`

### 5.1 Version Floor Check (`desktop-v2-check-version`)
- **Method:** `POST`
- **Payload:** `{ "version": "1.1.19", "platform": "windows", "architecture": "x86_64" }`
- **Response:**
  ```json
  {
    "minimum_version": "1.1.15",
    "latest_version": "1.1.20",
    "update_required": false,
    "download_url": "https://updates.trykuro.app/win/KuroWindows-win-1.1.20.nupkg"
  }
  ```

### 5.2 Device Attestation & Registration (`desktop-v2-register-device`)
- **Method:** `POST`
- **Payload:**
  ```json
  {
    "device_key_id": "...",
    "public_key": "...",
    "key_type": "ecdsa_p256",
    "tpm_endorsement_cert": "<base64_der>",
    "hardware_facets": {
      "motherboard_uuid": "...",
      "smbios_serial": "...",
      "cpu_name": "AMD Ryzen 9 7950X",
      "gpu_name": "NVIDIA GeForce RTX 4090",
      "is_vm": false
    }
  }
  ```

### 5.3 Desktop Sync (`desktop-v2-sync`)
- **Method:** `POST`
- **Payload:** `{ "config_version": 141, "device_id": "...", "platform": "windows" }`
- **Response:**
  ```json
  {
    "config_version": 142,
    "config": {
      "video": { "bitrate_kbps": 2500, "fps": 30 },
      "feature_flags": { "enable_gen2_capture": true, "system_audio": true },
      "masking": {
        "blocked_apps": ["keepass.exe", "1password.exe", "bitwarden.exe"],
        "sensitive_titles": ["bank", "medical", "tax", "payroll"]
      }
    }
  }
  ```

### 5.4 Segment Upload Pre-signed Request (`desktop-v2-upload-request`)
- **Method:** `POST`
- **Payload:**
  ```json
  {
    "session_id": "2026-11-02_18-30-00",
    "segment_index": 0,
    "files": [
      { "name": "clip_0.mp4", "size": 154829100, "sha256": "4a7c..." },
      { "name": "events_0.jsonl", "size": 142010, "sha256": "8f1b..." },
      { "name": "frame_timestamps_0.jsonl", "size": 98400, "sha256": "2e9a..." },
      { "name": "audio.m4a", "size": 4718200, "sha256": "3d5f..." }
    ]
  }
  ```
- **Response:**
  ```json
  {
    "upload_id": "upl_8a7d6e5c...",
    "files": {
      "clip_0.mp4": { "multipart": true, "part_urls": ["https://s3.us-east-1.amazonaws.com/...partNumber=1", "..."] },
      "events_0.jsonl": { "multipart": false, "put_url": "https://s3.us-east-1.amazonaws.com/..." }
    }
  }
  ```

### 5.5 Multipart Upload Finalization (`desktop-v2-upload-multipart-complete`)
- **Method:** `POST`
- **Payload:**
  ```json
  {
    "session_id": "2026-11-02_18-30-00",
    "segment_index": 0,
    "file_name": "clip_0.mp4",
    "upload_id": "upl_8a7d6e5c...",
    "parts": [
      { "part_number": 1, "etag": "\"d41d8cd98f00b204e9800998ecf8427e\"" },
      { "part_number": 2, "etag": "\"a8b6c4d7e2f1a90c4b8e7d6a5c4b3a2e\"" }
    ]
  }
  ```

---

## 6. External Telemetry & Hardware Services

### 6.1 AWS Telemetry Batch Gateway
- **Endpoint:** `https://rj4mawk0tj.execute-api.us-east-1.amazonaws.com/v1/batches`
- **Payload:** Gzip-compressed JSON array of UUIDv7 telemetry records emitted by `ClientTelemetry.dll`.

### 6.2 TPM Endorsement Key PKI Authorities
- **Intel EKOP:** `https://ekop.intel.com/ekcertservice/` (Used to validate Intel ME / fTPM endorsement certificates)
- **AMD fTPM:** `https://ftpm.amd.com/pki/aia/` (Used to validate AMD PSP fTPM endorsement certificates)

