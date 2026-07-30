# Music Power 3

A native Windows desktop music player built with **WinUI 3** (Windows App SDK), featuring local library scanning, ID3/metadata tag editing, artwork management, and full Windows System Media Transport Controls (SMTC) integration.

![Platform](https://img.shields.io/badge/platform-Windows-0078D4)
![.NET](https://img.shields.io/badge/.NET-10.0-512BD4)
![License](https://img.shields.io/badge/license-MIT-green)

## Features

- **Local library scanning** — add folders and scan them for audio files, with a persistent JSON-backed library cache for fast startup
- **Metadata tag editing** — view and edit title, artist, album, album artist, track/disc number, year, genre, and comments (single-track or batch edit), powered by [TagLib#](https://github.com/mono/taglib-sharp)
- **Artwork management** — fetch, replace, or clear embedded album artwork, with an in-memory image cache for smooth scrolling
- **Playback engine** — play/pause/next/previous, shuffle (with configurable shuffle memory) and repeat (off/all/one) modes, and "random date range" playback filtering
- **System Media Transport Controls (SMTC)** — media keys, hardware buttons, and the Windows volume flyout all control playback
- **Custom scrubbing progress bar** — a hand-built `AudioProgressBar` control supporting horizontal/vertical orientation, pointer scrubbing, and step snapping
- **Theming** — live accent color picker and adjustable global UI scale, both persisted between sessions
- **Search & sort** — instant library search/filter and configurable sort order
- **Settings persistence** — user preferences are saved to `%AppData%\MusicPower3\settings.json`
- **Bundled installer** — a lightweight WinForms-based setup wizard (`SetupWizard`) handles install, update, uninstall, Start Menu shortcuts, and Windows "Add/Remove Programs" registration

## Tech Stack

| Component | Technology |
|---|---|
| UI Framework | WinUI 3 / Windows App SDK |
| Runtime | .NET 10 (`net10.0-windows10.0.19041.0`) |
| Tag reading/writing | [TagLibSharp](https://www.nuget.org/packages/TagLibSharp) |
| Layout helpers | [CommunityToolkit.WinUI.Controls.LayoutTransformControl](https://www.nuget.org/packages/CommunityToolkit.WinUI.Controls.LayoutTransformControl) |
| Media playback | `Windows.Media.Playback.MediaPlayer` + `SystemMediaTransportControls` |
| Installer | .NET WinForms (`SetupWizard`), self-contained single-file publish |

## Project Structure

```
├── MusicPower3/              # Main WinUI 3 application
│   ├── App.xaml(.cs)         # Application entry point, hosts the AudioEngine singleton
│   ├── MainWindow.xaml(.cs)  # Main window: library view, player controls, settings, edit overlay
│   ├── AudioProgressBar.cs   # Custom scrubbable progress bar control
│   ├── Models.cs             # Track / AppSettings models, artwork loading & caching
│   ├── Services.cs           # AudioEngine (SMTC + MediaPlayer), settings/library persistence,
│   │                          # metadata reading & writing
│   ├── Program.cs            # Custom Main() (XAML generated main disabled)
│   └── app.manifest
└── SetupWizard/               # Standalone WinForms installer/uninstaller
    ├── MainForm.cs            # Install / update / uninstall UI and logic
    └── Program.cs
```

## Getting Started

### Prerequisites

- Windows 10, version 1903 (build 19041) or later
- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Windows App SDK](https://learn.microsoft.com/windows/apps/windows-app-sdk/) workload (installed via the Visual Studio installer, or the standalone SDK)
- Visual Studio 2022 (17.x or later) with the **.NET Desktop Development** and **Windows App SDK / WinUI application development** workloads — recommended, though `dotnet build` works from the CLI as well

### Build & Run

```bash
git clone https://github.com/<your-username>/MusicPower3.git
cd MusicPower3

# Build the main app
dotnet build MusicPower3/MusicPower3.csproj -c Release

# Run it
dotnet run --project MusicPower3/MusicPower3.csproj
```

### Publish a self-contained build

```bash
dotnet publish MusicPower3/MusicPower3.csproj -c Release -r win-x64 --self-contained true
```

### Building the installer

The `SetupWizard` project expects a `zipping\Payload.zip` (the published app output) embedded as a resource before it builds:

```bash
dotnet publish SetupWizard/SetupWizard.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

## Configuration

User settings and the library cache are stored outside the install directory, so they survive updates and reinstalls:

- `%AppData%\MusicPower3\settings.json` — theme, volume, shuffle/repeat state, window preferences
- `%AppData%\MusicPower3\library_cache.json` — cached scan results for fast startup

## Contributing

Contributions are welcome! Please:

1. Fork the repo and create a feature branch (`git checkout -b feature/my-feature`)
2. Keep changes focused and match the existing code style (nullable reference types enabled, implicit usings)
3. Open a pull request describing what changed and why

Bug reports and feature requests can be filed via [Issues](../../issues).

## License

This project is licensed under the **MIT License** — see [LICENSE](LICENSE) for details.

> **Note on dependencies:** The published build bundles [TagLibSharp](https://github.com/mono/taglib-sharp) (LGPL-2.1) inside the single-file executable, along with MIT-licensed components from the Windows App SDK and Windows Community Toolkit. See [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt) for full license texts, copyright notices, and instructions for obtaining or relinking against the source of each bundled LGPL component, as required by its license.

## Acknowledgments

- [Windows App SDK](https://github.com/microsoft/WindowsAppSDK)
- [TagLib#](https://github.com/mono/taglib-sharp)
- [CommunityToolkit.WinUI](https://github.com/CommunityToolkit/Windows)
