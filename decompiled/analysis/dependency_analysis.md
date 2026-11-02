# Dependency & Interop Analysis - Kuro Windows Desktop

> **Document:** `decompiled/analysis/dependency_analysis.md`  
> **Source Target:** `KuroWindows.deps.json`, `csharp_source/`, `native_libraries/`

---

## 1. Runtime Framework Dependencies

Kuro is built on **.NET 8.0 Windows Desktop** (`net8.0-windows10.0.19041.0`). It ships all required Base Class Libraries (BCL) directly within the package, enabling execution on machines without a pre-installed .NET SDK or runtime.

### 1.1 Core CLR Execution Stack
- **`coreclr.dll` (v8.0.32):** The execution engine, garbage collector initialization, and native-to-managed transition manager.
- **`clrjit.dll`:** The RyuJIT compiler, converting MSIL instructions into optimized x86_64 machine code at runtime.
- **`hostfxr.dll` & `hostpolicy.dll`:** Resolves dependencies from `KuroWindows.deps.json` and configures runtime switches from `KuroWindows.runtimeconfig.json`.

---

## 2. Managed NuGet Dependencies

| Package / Assembly | Version | Purpose in Kuro |
| :--- | :--- | :--- |
| **`Microsoft.Web.WebView2.Core`** | `1.0.x` | Core Chromium browser host engine integration |
| **`Microsoft.Web.WebView2.Wpf`** | `1.0.x` | WPF visual element hosting the Edge WebView2 control |
| **`Vortice.Direct3D11`** | `3.6.2` | Direct3D 11 hardware device creation, swapchains, texture copying |
| **`Vortice.DXGI`** | `3.6.2` | DXGI 1.2 Desktop Duplication API (`IDXGIOutputDuplication`) bindings |
| **`Vortice.MediaFoundation`** | `3.6.2` | Windows Media Foundation hardware-accelerated H.264 video encoding |
| **`SharpGen.Runtime`** | `2.1.0` | High-efficiency C#/COM interop layer powering Vortice |
| **`Velopack`** | `0.0.x` | Application update orchestration, delta downloads, execution stub |
| **`Microsoft.Windows.SDK.NET`** | `10.0.22621`| C#/WinRT projections for `Windows.Graphics.Capture` |

---

## 3. Bundled Native C/C++ Libraries

### 3.1 Fraunhofer FDK-AAC (`fdk-aac.dll`)
- **Role:** High-efficiency AAC audio encoder.
- **Function Exports:** `aacEncOpen`, `aacEncoder_SetParam`, `aacEncEncode`, `aacEncClose`, `aacEncInfo`.
- **Reason for Bundling:** Bypasses Windows Media Foundation AAC profile constraints and guarantees uniform audio compression across all Windows editions (including Windows N / Home / Pro / Enterprise).

### 3.2 WebView2 Native Loader (`WebView2Loader.dll`)
- **Role:** Binds the managed `Microsoft.Web.WebView2.Core.dll` to the installed system Edge runtime in `C:\Program Files (x86)\Microsoft\EdgeWebView\Application\`.

### 3.3 Microsoft QUIC (`msquic.dll`)
- **Role:** High-performance, cross-platform implementation of the IETF QUIC and HTTP/3 protocol, used for resilient network transport.

