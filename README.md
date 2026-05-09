# RoundedTB — macOS Dock Magnification

> **Windows 11 taskbar with smooth macOS-style icon hover magnification**, built on top of [RoundedTB](https://github.com/torchgm/RoundedTB).

![Build](https://github.com/iklchiooo/roundedtb-macos/actions/workflows/ci.yml/badge.svg)

---

## What this does

Adds a **macOS Dock magnification effect** to your Windows 11 taskbar:

- Icons (the AppList pill) smoothly expand as your cursor approaches
- Growth is **anchored to the taskbar edge** — bottom taskbar grows upward, matching macOS behaviour
- Scale and animation smoothness are both **configurable** in the UI
- The effect cleanly starts/stops without race conditions

---

## How to use (pre-built)

1. Download the latest zip from [Releases](https://github.com/iklchiooo/roundedtb-macos/releases)
2. Extract and run `RoundedTB.exe`
3. Tick **"macOS Dock magnification on hover"**
4. Drag the **Hover scale** slider to taste (default 70 % bigger at peak)

---

## Building from source

### Prerequisites

| Tool | Version |
|------|---------|
| Windows 11 (or 10 21H2+) | — |
| Visual Studio 2022 | with **.NET desktop development** workload |
| .NET 6 SDK | 6.0.x |

### Steps

```powershell
git clone https://github.com/iklchiooo/roundedtb-macos.git
cd RoundedTB-dockMagnification

dotnet restore RoundedTB.sln
msbuild RoundedTB.sln /p:Configuration=Release /p:Platform="Any CPU" /m
# Output: RoundedTB\bin\Release\net6.0-windows10.0.19041.0\RoundedTB.exe
```

### GitHub Actions (CI/CD)

Every push to `main`/`master` builds automatically.  
Pushing a tag like `v1.2.3` also creates a GitHub Release with a zip artifact.

---

## Architecture

| File | Purpose |
|------|---------|
| `DockMagnification.cs` | New — macOS hover magnification engine |
| `Taskbar.cs` | RoundedTB core — region shaping via `SetWindowRgn` |
| `LocalPInvoke.cs` | All Win32 P/Invoke declarations |
| `MainWindow.xaml(.cs)` | UI — includes new Dock Mag controls |
| `Types.cs` | Settings model — includes `DockMagnificationEnabled/MaxScale/Smoothing` |
| `Background.cs` | Background update loop |

---

## Credits

- Original [RoundedTB](https://github.com/torchgm/RoundedTB) by torchgm & contributors
- macOS Dock magnification feature added on top
