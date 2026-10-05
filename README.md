# Music Power 3

A native Windows desktop music player built with **WinUI 3** (Windows App SDK), featuring local library scanning, ID3/metadata tag editing, artwork management, MusicBee-inspired queueing, and deep Windows 11 system integration.

![Platform](https://img.shields.io/badge/platform-Windows%2011%20%7C%2010-0078D4)
![.NET](https://img.shields.io/badge/.NET-10.0-512BD4)
![Version](https://img.shields.io/badge/version-2.3.0.0-blue)
![License](https://img.shields.io/badge/license-MIT-green)

---

## Features

- **Windows 11 Media Controls & Quick Settings** — Full System Media Transport Controls (SMTC) integration with explicit Application User Model ID (`Elhoussain.MusicPower3`), displaying the application name, icon, and live track artwork in the Windows 11 volume flyout, lock screen, and Quick Settings without generic "Unknown app" labels.
- **Windows Explorer Integration** — Integrated into the Windows Explorer "Open with" context menu for all supported formats (`.mp3`, `.flac`, `.wav`, `.m4a`, `.aac`, `.ogg`, `.wma`).
- **Single-Instance Redirection** — Launching or opening an audio file while the player is active forwards the track directly to the running window without spawning duplicate instances.
- **MusicBee-Inspired Playback Queue** — Right-click any track to select "Play next" or "Add to queue" individually or in batch. Inspect and manage upcoming tracks via the dedicated transport queue flyout with an `InfoBadge` track counter, per-item removal, and clear controls.
- **Native Fluent UI with Mica** — Modern Windows 11 Mica backdrop with fluid responsive settings cards that adapt between compact and wide viewports without fixed dimensions or scaling distortions.
- **Adaptive Accent Color & Themes** — Automatically synchronizes with your Windows system accent color in real time (`UISettings.ColorValuesChanged`), or allows customizing a bespoke theme color with instant palette regeneration.
- **Vector AudioProgressBar** — High-performance vector progress bar with hover states, smooth scrubbing, full keyboard navigation (arrows, Page Up/Down, Home/End), and screen-reader accessibility (`IRangeValueProvider` automation peer).
- **Local Library Scanning** — Streaming folder scanner with timestamp caching and bounded concurrency (`MaxDegreeOfParallelism = 2`) for fast startup and low CPU/SSD usage.
- **Metadata Tag Editing** — View and edit title, artist, album, album artist, track/disc number, year, genre, and comments with single-track or batch editing, powered by [TagLib#](https://github.com/mono/taglib-sharp).
- **Artwork Management** — Fetch online metadata with security-hardened HTTPS requests, host whitelisting, payload size limits, and an LRU artwork cache with capped decode dimensions (96px thumbnails, 600px high-resolution).
- **Hardened Installer & Uninstaller** — Self-contained setup wizard with zip-slip directory traversal guards, manifest-based file removal, and protected uninstall path validation.

---

## Tech Stack

| Component | Technology |
|---|---|
| UI Framework | WinUI 3 / Windows App SDK (1.6+) |
| Runtime | .NET 10 (`net10.0-windows10.0.19041.0`) |
| Tag reading/writing | [TagLibSharp](https://www.nuget.org/packages/TagLibSharp) (2.3.0) |
| Layout helpers | [CommunityToolkit.WinUI.Controls.LayoutTransformControl](https://www.nuget.org/packages/CommunityToolkit.WinUI.Controls.LayoutTransformControl) |
| Media playback | `Windows.Media.Playback.MediaPlayer` + `SystemMediaTransportControls` |
| Installer | .NET WinForms (`SetupWizard`), self-contained single-file publish |

---

## Project Structure

```
├── App.xaml(.cs)              # Application entry point and audio engine lifecycle
├── MainWindow.xaml(.cs)       # Main window: library view, player controls, settings cards, edit overlay
├── AudioProgressBar.cs        # Custom accessible vector scrubbable progress bar
├── Models.cs                  # Track / AppSettings models, LRU artwork caching & decoding
├── Services.cs                # AudioEngine (SMTC + MediaPlayer), atomic settings/cache stores
├── Program.cs                 # Main entry point with AUMID, single-instance redirection & shell registration
├── app.manifest               # DPI awareness and Windows compatibility manifest
├── MusicPower3.csproj         # Main application project file
└── SetupWizard/               # Standalone WinForms installer/uninstaller
    ├── MainForm.cs            # Setup wizard UI, extraction, shortcut creation, registry associations
    └── Program.cs
```

---

## Getting Started

### Prerequisites

- Windows 10, version 1903 (build 19041) or Windows 11
- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- Windows App SDK runtime / build tools

### Build & Run from CLI

```bash
# Clone the repository
git clone https://github.com/Webpagemanager/Music-Power-3.git
cd Music-Power-3

# Build the application
dotnet build MusicPower3.csproj -c Release

# Run the player
dotnet run -c Release --no-build
```

### Building the Standalone Installer

To publish the application and generate `MusicPower3-Installer.exe`:

```bash
# 1. Publish the self-contained app
dotnet publish MusicPower3.csproj -c Release -r win-x64 --self-contained true

# 2. Package the published files into the installer payload
Compress-Archive -Path "bin\x64\Release\net10.0-windows10.0.19041.0\win-x64\publish\*" -DestinationPath "SetupWizard\zipping\Payload.zip" -Force

# 3. Publish the setup wizard as a standalone executable
dotnet publish SetupWizard/SetupWizard.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true

# 4. Copy the generated installer to the root
Copy-Item "SetupWizard\bin\Release\net10.0-windows\win-x64\publish\SetupWizard.exe" "MusicPower3-Installer.exe" -Force
```

---

## Configuration & Cache

User settings and the library cache are stored outside the installation folder, persisting across updates:

- `%AppData%\MusicPower3\settings.json` — theme preferences, volume, shuffle/repeat state, layout settings
- `%AppData%\MusicPower3\library_cache.json` — cached library track metadata for instant startup

---

## Contributing

Contributions are welcome. Please:

1. Fork the repo and create a feature branch (`git checkout -b feature/my-feature`)
2. Keep changes focused and match the existing architectural patterns
3. Open a pull request describing the changes and test results

Bug reports and feature requests can be filed on [GitHub Issues](https://github.com/Webpagemanager/Music-Power-3/issues).

---

## License

This project is licensed under the **MIT License** — see [LICENSE](LICENSE) for details.

> **Note on dependencies:** The build bundles [TagLibSharp](https://github.com/mono/taglib-sharp) (LGPL-2.1) along with MIT-licensed components from the Windows App SDK and Windows Community Toolkit. See [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt) for license texts and instructions for obtaining or relinking against bundled components.
