# Random Media Player

A unified WPF slideshow application for photos and videos. Pick one or more folders, choose a mode, hit Start — the app picks files at random, applies your filters, and plays them on a chosen monitor (or just the preview pane).

Built on **.NET 9** with native dark mode (`ThemeMode="Dark"`), VLC for video, and WPF's built-in `Image` / `MediaElement` for stills and animated GIFs.

This is a unification of two earlier programs (`RandomSlideshow` and `RandomVideoshow`) into one app — both feature sets are preserved and a few quality-of-life features were added on top.

---

## Features

### Three modes
- **Photos** — static images only.
- **Videos** — VLC-backed playback with random clip-start time and dimension filters.
- **Mixed** — both at once, chosen at random per file.

### Multi-folder support
Add as many folders as you want with the Vista folder dialog (multi-select supported). Recursive enumeration walks the entire tree. Optional **Persist folders** checkbox saves the list between runs.

### Robust enumeration
Uses eager `Directory.GetFiles` / `Directory.GetDirectories` with separate per-step try/catch, so a single permission-denied or OneDrive sync placeholder can't truncate the rest of the scan. Per-folder counts and total scanned/matched are written to Debug output, and the progress label shows `Matched X of Y files (N%)` while running so you can spot a too-tight extension filter immediately.

### File-format coverage

| Group | Extensions | Notes |
|---|---|---|
| Static photos (always) | `.jpg .jpeg .jpe .jif .png .bmp .dib .tif .tiff .webp .heic .heif .ico .wdp .jxr` | `.heic` / `.webp` need the free Microsoft Store codec packs to decode |
| Animated images (opt-in) | `.gif .gifv .jfif .apng` | Toggled by **Include animated** checkbox. In Photo mode they show as a static first frame; in Video / Mixed they animate via `MediaElement` |
| Videos (always when in Video / Mixed) | 30+ container formats including `.mp4 .mkv .mov .avi .webm .wmv .ts .m2ts .mts .vob .flv` etc. | Played through VLC |

### Filters
- **Orientation**: All / Landscape / Vertical (works for both photos and videos)
- **Min size** (W × H) for videos — filters out clips below the chosen pixel dimensions
- **Mute** for video playback
- **Always on top** for both windows
- **Scale to Fill** vs. uniform fit

### Display
- Choose a monitor from the dropdown, or pick **None — preview only** to keep playback inside the main window.
- Click anywhere on the preview pane to expand it to fill the main window.
- Fullscreen window auto-closes on Esc; slideshow keeps running.

### EXIF rotation for photos
Honors `System.Photo.Orientation` — sideways and upside-down photos display the right way up.

### Sleep prevention
While the slideshow is running, `SetThreadExecutionState` keeps the system + display awake. Released on stop or close.

---

## Keyboard shortcuts

### Global hotkeys (work even when the app isn't focused)

| Key | Default | Description |
|---|---|---|
| **Panic** | `F11` | Configurable action — Stop slideshow or Exit application. |
| **Hold** | `F9` | Pauses the slideshow timer. Photo/GIF stays put; video loops on its current clip. Press again to release. |

Both keys support optional **Ctrl** and **Alt** modifiers to avoid conflicts.

The status indicator next to each combo says `✓ Active` (green) when the hotkey was registered successfully or `⚠ Unavailable` (orange) when something else has already claimed the combo system-wide. Common culprits: NVIDIA ShadowPlay / GeForce Experience, capture/streaming tools, hardware-vendor drivers.

#### Multi-instance hotkeys (checkbox)
By default the global hotkeys use `RegisterHotKey`, which is **exclusive** — only one process on the system can own a given combo. If you run multiple copies of this app, only the first will respond.

Tick **Multi-instance** to switch to a `WH_KEYBOARD_LL` low-level keyboard hook instead — every running instance installs its own observer, so all of them respond to the key. Trade-off: the LL hook can't consume the keystroke without breaking sibling hooks, so the focused application *also* receives the key. Pick a key with no app-level action (F9 is excellent), or add Ctrl/Alt modifiers.

### Focus-based shortcuts (preview pane or fullscreen window)

| Key | Description |
|---|---|
| **Space** | Stop the slideshow |
| **Delete** | Hold + confirm + permanently delete the current file. Advances to a different file first so VLC releases the file lock, waits 250 ms, then `File.Delete`. |
| **→** Right arrow | Skip to the next random item; the current item is pushed onto history |
| **←** Left arrow | Step back through history (up to 5 items kept) |
| **Esc** (fullscreen only) | Close the fullscreen window; slideshow keeps running |

These keys are intentionally suppressed when a TextBox or ComboBox has focus so editing keeps working normally.

---

## Settings

User preferences are persisted to `%APPDATA%\RandomMediaPlayer\settings.json` on close, loaded on startup. A first-run with no file uses defaults.

What's saved:
- Mode, orientation filter, animated-include toggle
- Display time / clip length, min-size for videos
- Always-on-top, scale-to-fill, mute
- Selected monitor (matched by device name; falls back to None if the saved monitor disappears)
- Panic combo (key + modifiers + action)
- Hold combo (key + modifiers)
- Multi-instance hotkeys flag
- Persist folders flag
- Folder list (only if Persist folders is on)

---

## Build

### Requirements
- **.NET 9 SDK** (or newer)
- **Windows 10/11** (uses WPF + `ThemeMode="Dark"`, requires Windows desktop runtime)
- **VLC media player** installed somewhere — see below

### Building from source
```pwsh
cd RandomMediaPlayer
dotnet build RandomMediaPlayer.sln
```
Output goes to `RandomMediaPlayer\bin\Debug\net9.0-windows\RandomMediaPlayer.exe`.

For Release: `dotnet build -c Release`.

### VLC dependency
Video playback is provided by `Vlc.DotNet.Wpf` 3.1.0, which **does not ship the native libvlc binaries**. The app finds libvlc at runtime by checking, in order:

1. `<exe>\libvlc\win-x64\` (next to the executable, useful for distribution)
2. `<exe>\win-x64\` (alternative layout)
3. `C:\Program Files\VideoLAN\VLC\` (system 64-bit install)
4. `C:\Program Files (x86)\VideoLAN\VLC\` (system 32-bit fallback)

The simplest path: install [VLC media player](https://www.videolan.org/vlc/) from videolan.org (64-bit if your Windows is 64-bit). The app will pick it up automatically. If VLC isn't found, photo / GIF playback still work — only videos show the "libvlc not found" warning.

---

## Architecture notes

### Project layout
```
RandomMediaPlayer/
├── RandomMediaPlayer.sln
└── RandomMediaPlayer/
    ├── App.xaml(.cs)
    ├── MainWindow.xaml(.cs)             — main UI, all hotkey infrastructure, settings load/save
    ├── FullscreenSlideshowWindow.xaml(.cs) — secondary window for fullscreen playback
    ├── AppSettings.cs                   — JSON-serialized user preferences
    ├── AssemblyInfo.cs
    └── RandomMediaPlayer.csproj
```

### Media classification
`ClassifyMedia(path)` is mode-aware:
- `.webm` and other video extensions → `MediaKind.Video` (always plays via VLC)
- `.gif` / `.jfif` / `.gifv` / `.apng` → `MediaKind.Image` in Photo mode (static first frame), `MediaKind.Gif` in Video / Mixed mode (animated via `MediaElement`)
- Everything else → `MediaKind.Image`

### Playback transitions and the "freeze on second video" bug
libvlc's `Stop` synchronously waits for its internal media thread to wind down. Calling `Stop` while `EndReached` is firing deadlocks the UI thread. The current code never calls `Stop` on a player it's about to reuse — `Play(newUri)` handles the transition itself — and it uses `Pause` (not `Stop`) when switching *away* from VLC, since `Pause` doesn't tear down the media thread. EndReached / MediaEnded handlers are gated on `_currentMediaKind` so stale events from a paused-but-still-buffered player can't accidentally trigger an advance.

### Hotkey modes
- **Single-instance (default)**: `user32!RegisterHotKey` with `MOD_NOREPEAT`. WM_HOTKEY arrives via `HwndSource.AddHook`. Mutually exclusive system-wide.
- **Multi-instance (opt-in)**: `user32!SetWindowsHookEx WH_KEYBOARD_LL` per process. Hook callback compares `vkCode` + current modifier state (read via `GetKeyState`) against snapshot fields of the panic/hold combos, marshals to UI via `Dispatcher.BeginInvoke`. Returns `CallNextHookEx` unconditionally so sibling instances also receive the event.

`InstallHotkeys()` / `UninstallAllHotkeys()` are the single entry points and tear down whichever flavor was previously active before installing the new one.

---

## Known limitations

- HEIC and WebP need the free Microsoft "HEIF Image Extensions" / "Webp Image Extensions" packages from the Microsoft Store to actually decode. Without them, those files are enumerated but skipped at load time.
- Multi-instance mode passes the keystroke through to the focused app — see the table above. This is unavoidable with LL hooks.
- The MediaInfo NuGet package surfaces a build-time `NETSDK1206` warning about Linux RIDs. Harmless on Windows.
