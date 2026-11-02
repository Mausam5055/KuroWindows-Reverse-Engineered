# Package & Container Structure Analysis - Kuro Windows Desktop

> **Document:** `decompiled/analysis/package_structure.md`  
> **Source Target:** `d:\Kuro and Loggy3\decompiled\original\KuroWindows-win-Setup.exe`

---

## 1. Setup Executable Anatomy

`KuroWindows-win-Setup.exe` is a modern **Velopack self-extracting distribution container** designed for silent per-user installation without requiring administrative privileges (UAC prompt).

```
+-------------------------------------------------------------+
| PE Header (MZ / PE32+ x64 Stub)                            |
| Size: ~1.2 MB                                               |
| Purpose: Extracts runtime and executes update hooks         |
+-------------------------------------------------------------+  <-- Offset 1,262,248 (0x1342E8)
| Embedded NuGet Archive (.nupkg / PK Zip Container)          |
| Magic: PK\x03\x04                                           |
| Size: ~85.7 MB                                              |
|                                                             |
| ├── [Content_Types].xml                                     |
| ├── KuroWindows.nuspec (Metadata manifest)                  |
| ├── setup.ico                                               |
| └── lib/app/ (Payload root)                                 |
|     ├── KuroWindows.exe (Host launcher stub)                |
|     ├── KuroWindows.dll (Core application assembly)         |
|     ├── ClientTelemetry.dll                                 |
|     ├── DeviceIdentity.dll                                  |
|     ├── SystemAudio.dll                                     |
|     ├── DesktopUpdates.dll                                  |
|     ├── DesktopStatus.dll                                   |
|     ├── fdk-aac.dll (Native AAC codec)                      |
|     ├── WebView2Loader.dll (Native Edge bootstrapper)       |
|     ├── coreclr.dll + .NET 8 Runtime Engine                 |
|     ├── ui/ (HTML5 / ES6 sandboxed frontend)                |
|     └── localized satellite resource assemblies             |
+-------------------------------------------------------------+
```

---

## 2. Extraction & Physical File Layout

The package extracts cleanly into a self-contained runtime folder:
- **Executable Binaries:** 2 launcher executables (`KuroWindows.exe`, `KuroWindows_ExecutionStub.exe`).
- **Managed Assemblies:** 6 first-party application assemblies + 492 .NET Core / Windows Desktop BCL assemblies.
- **Native Interop Libraries:** 13 standalone native DLLs including Fraunhofer FDK-AAC, WebView2 Loader, Microsoft QUIC, and Direct3D runtime compilers.
- **Frontend Presentation Layer:** 7 core web files (`index.html`, `app.js`, `communications.js`, `styles.css`, `colors_and_type.css`, SVG icons) and 3 variable font families (`Satoshi`, `Outfit`, `GeistMono`).
- **Localization:** 15 regional language satellite directories (`cs`, `de`, `es`, `fr`, `it`, `ja`, `ko`, `pl`, `pt-BR`, `ru`, `tr`, `zh-Hans`, `zh-Hant`).

