# Application Metadata & Binary Identity - Kuro Windows Desktop

> **Document:** `APP_METADATA.md`  
> **Source Target:** `d:\Kuro and Loggy3\KuroWindows-win-Setup.exe`

---

## 1. Setup Executable Cryptographic Hashes

| Property | Value |
| :--- | :--- |
| **File Name** | `KuroWindows-win-Setup.exe` |
| **File Size** | `86,977,400` bytes (~82.95 MiB) |
| **MD5 Digest** | `88f33f838f6bdcf49738377b5de989ee` |
| **SHA-1 Digest** | `92e0516fd8ac0d98cd43b3940507b8e448064b09` |
| **SHA-256 Digest**| `b4758da982fcfb818d0b174c4e9a5a2d5fd487b4a642428204820b7abe2134fc` |
| **Format** | Windows PE32+ (x64) Self-Extracting Velopack Installer Stub |
| **Embedded Archive Offset** | `1,262,248` bytes (ZIP / PK header `PK\x03\x04`) |

---

## 2. NuGet Packaging Specification (`KuroWindows.nuspec`)

```xml
<?xml version="1.0" encoding="utf-8"?>
<package xmlns="http://schemas.microsoft.com/packaging/2010/07/nuspec.xsd">
  <metadata>
    <id>KuroWindows</id>
    <title>Kuro</title>
    <description>Kuro</description>
    <authors>Kuro</authors>
    <version>1.1.19</version>
    <channel>win</channel>
    <mainExe>KuroWindows.exe</mainExe>
    <os>win</os>
    <rid>win-x64</rid>
    <runtimeDependencies>webview2</runtimeDependencies>
    <shortcutLocations>Desktop,StartMenuRoot</shortcutLocations>
    <shortcutAmuid>velopack.KuroWindows</shortcutAmuid>
    <machineArchitecture>x64</machineArchitecture>
  </metadata>
</package>
```

---

## 3. Core Assembly Metadata & Hashes

| Assembly / Binary | Size (Bytes) | SHA-256 Digest | MVID / Build Token |
| :--- | :--- | :--- | :--- |
| **`KuroWindows.dll`** | `1,974,592` | `5fb5416b5df6761f730a5579ae2c624ee015dd84277bfa495349ed29ae9766e4` | `b332789b-a4e7-4377-aa6c-216c12dd33c6` |
| **`ClientTelemetry.dll`** | `124,224` | `ea36be66326ffc622d7a670d13e98fc2b3b05488f45224bdf2b3b145522ccbe8` | `b1ec8792-7484-4860-b6aa-5f913d8d6447` |
| **`DeviceIdentity.dll`** | `119,616` | `0cdd7b4b0ae2eb1a22f9b268a45db2f9692a6e18d23ee3902aa36c29b8050cae` | `905479fa-3f1b-4d43-9828-9366df65e186` |
| **`SystemAudio.dll`** | `100,672` | `70cca52a07754d6a016fc8f9c3dd44e5946258b3dda10264257942f503c7ff7f` | `435d8e78-43bb-4277-bc60-d798fe111b11` |
| **`DesktopUpdates.dll`** | `79,680` | `e62d7acd685e911fac5c2119a96bbe18d2b8e11ea0faef4deb9e2783f06b32b4` | `3781be92-e4d0-4bf6-905c-d2ff8ea998aa` |
| **`DesktopStatus.dll`** | `26,944` | `c9fe405bebe967f668afa5c25614fbf65e92d26a5f1966876c94f44e0ba63e3b` | `5dc227aa-85b4-4e44-b26a-9f5da0e8f3a1` |
| **`fdk-aac.dll`** | `1,753,408` | `31572b8db14502888512f5a683ca1fcdfb04e02f4fd69fb5f4c4a867423c5e6c` | Native C Export Library |
| **`WebView2Loader.dll`** | `160,320` | `8427b1fc58ec707813e5c0a51eb5d69397bb333250a7b891be4d3b123f1e0f1c` | Native Bootstrapper |

---

## 4. Build Environment & Original Repository Path

Extracted debug symbols and internal logging paths establish the original CI/CD build infrastructure:
* **Repository Name:** `markov-screen-recorder-tool`
* **Original Build Server Root:** `D:\a\markov-screen-recorder-tool\markov-screen-recorder-tool\kuro\windows\src\`
* **Target Framework Moniker (TFM):** `net8.0-windows10.0.19041.0`
* **Target Runtime Identifier (RID):** `win-x64`
* **Compiler:** Microsoft Visual C# Compiler version `4.8.0` / Roslyn

