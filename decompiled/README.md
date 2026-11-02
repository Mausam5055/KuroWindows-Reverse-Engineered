# Kuro Windows - Decompiled & Reconstructed Source Package

> **Target Artifact:** `KuroWindows-win-Setup.exe`  
> **Application Title:** Kuro  
> **Package Identifier:** `KuroWindows`  
> **Package Version:** `1.1.19.0`  
> **Target Framework:** `.NETCoreApp,Version=v8.0` (`net8.0-windows`)  
> **Architecture:** `win-x64`  
> **Packager:** Velopack (`vpk`) with NuGet packaging specification

---

## 1. Directory Structure Overview

This directory contains the completely extracted, decompiled, structured, and verified source code, UI presentation assets, native libraries, and analytical reports for the Kuro Windows desktop application:

```text
decompiled/
│
├── README.md                          # This document
├── APP_METADATA.md                    # Core artifact identity, PE metadata, and cryptographic hashes
│
├── original/
│   └── KuroWindows-win-Setup.exe      # Pristine original setup executable (SHA-256 verified)
│
├── csharp_source/                     # Decompiled C# source projects (ILSpy 11.2 Engine)
│   ├── KuroWindows/                   # Main WPF host application, coordinators, capture & bridge
│   ├── ClientTelemetry/               # Telemetry batching engine, UUIDv7, on-disk ring buffer
│   ├── DeviceIdentity/                # TPM 2.0 CNG key manager, SMBIOS facet auditor, proofs
│   ├── SystemAudio/                   # WASAPI loopback capture, lock-free SampleRing, AAC writer
│   ├── DesktopUpdates/                # Velopack feed downloader, update staging, VersionFloor
│   └── DesktopStatus/                 # Windows Notification Area (system tray) icon integration
│
├── ui/                                # WebView2 frontend presentation application
│   ├── index.html                     # HTML5 entrypoint with strict CSP (connect-src 'none')
│   ├── app.js                         # Core state machine, 20 design states, CoreBridge promise client
│   ├── communications.js             # Verifiable bulletin & guard notice presentation engine
│   ├── styles.css                     # Primary UI stylesheet and layout rules
│   ├── colors_and_type.css            # CSS custom properties (variables) for Light & Dark themes
│   ├── fonts/                         # Satoshi, Outfit, and GeistMono variable font assets
│   └── assets/                        # SVG application icons and brand marks
│
├── native_libraries/                  # Extracted native C/C++ dynamic link libraries
│   ├── fdk-aac.dll                    # Standalone Fraunhofer FDK AAC audio compression codec
│   ├── WebView2Loader.dll             # Microsoft Edge WebView2 native loader bootstrapper
│   ├── msquic.dll                     # Microsoft QUIC library for HTTP/3 transport
│   ├── coreclr.dll                    # .NET 8 CoreCLR execution runtime
│   ├── clrjit.dll                     # .NET 8 RyuJIT compiler
│   ├── clrgc.dll                      # .NET 8 Garbage Collector
│   ├── hostfxr.dll                    # .NET host resolver
│   ├── hostpolicy.dll                 # .NET host policy engine
│   ├── D3DCompiler_47_cor3.dll        # Direct3D shader compiler
│   ├── wpfgfx_cor3.dll                # WPF native graphics composition engine
│   └── PresentationNative_cor3.dll    # WPF Win32 messaging support library
│
├── raw_assemblies/                    # Original uncompressed .NET PE assemblies
│   ├── KuroWindows.dll                # Main application assembly (1.97 MB)
│   ├── ClientTelemetry.dll            # Telemetry assembly (124 KB)
│   ├── DeviceIdentity.dll             # Hardware identity assembly (119 KB)
│   ├── SystemAudio.dll                # Audio capture assembly (100 KB)
│   ├── DesktopUpdates.dll             # Update service assembly (79 KB)
│   ├── DesktopStatus.dll              # Tray icon assembly (26 KB)
│   └── ...                            # 400+ runtime framework & dependency assemblies
│
├── resources/                         # Packaging manifests, assets, and localized satellites
│   ├── Assets/                        # Windows App Store and Splash Screen PNG assets
│   ├── KuroWindows.nuspec             # NuGet packaging manifest
│   ├── KuroWindows.deps.json          # .NET dependency tree and assembly versions
│   ├── KuroWindows.runtimeconfig.json # .NET 8 runtime configuration options
│   └── localized_resources/          # Satellite resource assemblies (cs, de, es, fr, it, ja, ko, etc.)
│
├── analysis/                          # Technical analysis and reverse-engineering blueprints
│   ├── package_structure.md           # Deep dive into installer format and package layout
│   ├── dependency_analysis.md         # Framework, package, and native library dependencies
│   ├── component_analysis.md          # Subsystem breakdown and class responsibilities
│   ├── permissions_analysis.md        # Win32 capabilities, hooks, UAC, UIA, TPM, audio access
│   ├── security_analysis.md           # Cryptographic attestation, DPAPI, CSP sandbox, Anti-VM
│   └── decompilation_report.md        # Tool versions, file counts, and completeness assessment
│
└── logs/                              # Audit and verification logs
    ├── extraction.log                 # Installer extraction and payload inventory log
    ├── ilspy.log                      # ILSpy decompilation execution and warning log
    └── verification.log               # Cryptographic SHA-256 verification and cross-checks
```

---

## 2. Key Technical Findings

1. **Self-Extracting Setup Packaging:** `KuroWindows-win-Setup.exe` is a Velopack self-extracting stub executable containing an embedded Zip archive (`KuroWindows.nuspec`) starting at byte offset `1,262,248`.
2. **True Source Reconstruction:** All 6 first-party C# assemblies (`KuroWindows`, `ClientTelemetry`, `DeviceIdentity`, `SystemAudio`, `DesktopUpdates`, `DesktopStatus`) have been decompiled to fully navigable C# project trees (`.csproj` and `.cs` files) with zero missing classes.
3. **Frontend Separation:** The user interface is completely decoupled from the native backend. It runs in Microsoft Edge WebView2, mapping the local `ui/` directory to `https://kuro-app.example/` with a Content Security Policy forbidding direct network access (`connect-src 'none'`). All actions communicate through the bi-directional .NET `CoreBridgeServer`.
4. **Hardware Attestation:** The application uses Windows CNG to generate non-exportable ECDSA P-256 hardware keys in physical TPM 2.0 chips, submitting signed device proofs (`kuro-device-proof/v1`) along with Intel EKOP or AMD fTPM Endorsement Certificates to prevent virtual machine emulation and multi-accounting fraud.
5. **Continuous Telemetry & Ingestion:** Recording operates in 300-second immutable segments containing H.264 video, Fraunhofer FDK-AAC audio, 45-field input events (`events_*.jsonl`), and frame sidecars (`frame_timestamps_*.jsonl`). Sealed segments upload directly to Amazon S3 via single-part or multipart uploads.

