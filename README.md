# ClickSnap 🖱️

**Take a screenshot on every mouse click.**

ClickSnap listens to raw mouse events via Linux evdev (`/dev/input/event*`) and automatically saves a full-screen screenshot whenever you press the left, right or middle mouse button. Useful for collecting click datasets, activity logging, UX research and more.

Built with [.NET 10](https://dotnet.microsoft.com/) and [Avalonia](https://avaloniaui.net/).

> ⚠️ Linux only (Wayland & X11). All processing happens locally — no data ever leaves your machine.

![platform](https://img.shields.io/badge/platform-Linux-blue)
![dotnet](https://img.shields.io/badge/.NET-10-512BD4)
![avalonia](https://img.shields.io/badge/UI-Avalonia-11-8A2BE2)
![license](https://img.shields.io/badge/license-AGPL--3.0-green)

## ✨ Features

- 🖱️ Capture on **any mouse button** press (left / right / middle)
- 📁 Custom output folder, **persisted between runs** (`~/.config/ClickSnap/settings.txt`)
- 🚀 **Auto-start** recording when a folder is already configured
- ⏱️ Configurable **delay** before capture, so window switching has time to finish
- 🛡️ **Anti-spam**: clicks are ignored while a screenshot is pending / in progress
- 🌍 Works on **Wayland** (wlroots and GNOME/KDE via XDG Desktop Portal) and **X11**
- 🧹 Automatically removes GNOME's duplicate screenshots from `~/Pictures`

## 🔧 How it works

Mouse events are read directly from evdev, so click detection works on any display server. For the actual screen capture the best available tool is picked automatically:

| Environment | Screenshot backend |
|---|---|
| Wayland · wlroots (Sway, Hyprland, Wayfire, river) | `grim` |
| GNOME (Wayland / X11) | XDG Desktop Portal via `gjs` |
| KDE Plasma | `spectacle` |
| X11 (any DE) | `scrot` / `maim` / `import` (ImageMagick) / `gnome-screenshot` |

## 📦 Requirements

- Linux
- .NET SDK 10.0 (or retarget `net8.0` in `ClickSnap.csproj`)
- Read access to `/dev/input/event*` — add your user to the `input` group:

  ```bash
  sudo usermod -aG input $USER
  # then log out and log back in
  ```

- At least one screenshot tool for your environment (most are preinstalled):
  `grim`, `gjs`, `spectacle`, `scrot`, `maim`, `imagemagick`, `gnome-screenshot`

##  Build & Run

```bash
git clone https://github.com/YOUR_USERNAME/ClickSnap.git
cd ClickSnap
dotnet run -c Release
```

Publish a self-contained binary:

```bash
dotnet publish -c Release -r linux-x64 --self-contained
```

## 🖼️ Usage

1. Launch the app.
2. Press **Browse…** and pick a folder for screenshots.
3. Press **Start** (on the next launch recording starts automatically).
4. Click anywhere — each click saves a file like `screenshot_20260812_062700_0001.png`.

### Configuration

| Setting | Where |
|---|---|
| Output folder | `~/.config/ClickSnap/settings.txt` |
| Capture delay | `ScreenshotDelayMs` constant in `MainWindow.axaml.cs` (default `500` ms) |

## 🐛 Troubleshooting

- **«No mouse device found»** — you don't have access to `/dev/input/event*`. Add yourself to the `input` group (see above) and re-login.
- **`compositor doesn't support wlr-screencopy-unstable-v1`** — you're not on a wlroots compositor; ClickSnap automatically falls back to the XDG Desktop Portal.
- **Screenshots appear in `~/Pictures` instead of my folder** — make sure `gjs` is installed (it ships with GNOME) and update to the latest version.

## 💖 Donate

If you find ClickSnap useful, consider supporting its development:

**[☕ Donate via DonatePay](https://new.donatepay.ru/en/@1198774)**

There is also a **Donate** button inside the app.

## 📄 License

GNU Affero General Public License v3.0 (AGPL-3.0). See [LICENSE](LICENSE) for details.

