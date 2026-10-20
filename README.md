# Glass Mixer

A desktop volume mixer for Windows 10 and 11 that controls every app's sound
separately, from a see-through glass window with an adjustable blur. It starts
with Windows and lives in the tray.
Built with **C# (.NET 10) + WPF + WebView2 + NAudio**.

The design follows the interactive mockup in [`docs/mockup.html`](docs/mockup.html).

## Features

- A volume, mute, live level meter, **balance** and **output device** for every app playing sound
- Master volume, and switching the Windows default output device
- **Profiles:** saved mixes you apply with one click
- **List** and **Mixer** (vertical faders) layouts, and Compact, Regular and Wide sizes
- Glass with adjustable **blur (0–40px)** and **transparency**, smooth 18px corners and a soft shadow
- Minimizes to a small bar; remembers its position and size
- Lives in the tray, with a live tooltip, **Start with Windows**, and global hotkeys

## Install

Download `GlassMixer-<version>-win-x64.zip` from the Releases page, unzip it, and double-click
**Install Glass Mixer.cmd**.
- It installs for your user only, with no admin rights needed.
- It adds a **desktop icon** and a Start Menu shortcut, and appears in **Settings > Apps**, where you can uninstall it.
- It turns on **Start with Windows**. Turn it off from the tray icon's right-click menu.
- It needs the Microsoft Edge WebView2 Runtime, which ships with Windows 11.

## Use it

- **Ctrl+Alt+V** shows or hides the window. **Ctrl+Alt+M** minimizes it to the small bar.
- **✕ hides it; it keeps running in the tray.** Open it again by:
  - double-clicking the **desktop icon** or opening **Glass Mixer** from the Start menu (it always opens the full mixer), or
  - clicking the tray icon (it may be under the **^** arrow by the clock), or
  - pressing **Ctrl+Alt+V**.
- To always see the tray icon: **Settings > Personalization > Taskbar > Other system tray icons > Glass Mixer: On**.
- Right-click the tray icon for **Start with Windows** and **Quit**. After Quit, open it from the Start menu.
- Drag a slider, or scroll the mouse wheel over a row, to change that app's real volume.
- ▾ on an app opens its output device and balance. The sliders icon opens appearance settings.
- While the blur is above 0px, the mixer is left out of screenshots and screen sharing (see `BackdropBlur.cs`).

## Build it

```bash
dotnet run --project src/GlassMixer.App
```

To make an installable release (self-contained, so the PC doesn't need .NET installed):

```bash
powershell -ExecutionPolicy Bypass -File tools/build-release.ps1
```

It writes `dist/GlassMixer-<version>-win-x64/` and a `.zip` of it. The version comes from `<Version>` in `GlassMixer.App.csproj`.
`installer/GlassMixer.iss` makes a classic `setup.exe` instead, if you install [Inno Setup](https://jrsoftware.org/isinfo.php).

Other tools:
- `tools/make-icon.ps1` redraws `app.ico`.
- `tools/get-fonts.ps1` downloads the bundled fonts again.

To try the audio code without any window:

```bash
dotnet run --project src/GlassMixer.Probe
dotnet run --project src/GlassMixer.Probe -- set msedge 40
```

## How the pieces fit

```
src/
  GlassMixer.Core/       the audio engine (no UI)
    AudioService.cs        reads and changes volumes through the Windows Core Audio API
    AudioModels.cs         the data shapes: AudioApp, MixerSnapshot, OutputDevice
    DefaultDevice.cs       switches the Windows default output device
    SessionChannels.cs     per-app balance (left/right)
    AppOutput.cs           per-app output device
  GlassMixer.Probe/      a console app that tests Core on its own (step 1)
  GlassMixer.App/        the desktop app
    App.xaml.cs            startup: one copy only, tray icon, never quits on close
    MainWindow.xaml(.cs)   the glass window, WebView2, and the message bridge
    BackdropBlur.cs        the adjustable blur behind the window
    Native.cs              Windows API calls: transparency, hotkeys, window drag
    TrayIcon.cs            the tray icon, its tooltip and menu
    StartWithWindows.cs    the "Start with Windows" setting
    WindowSettings.cs      remembers position and size
    IconCache.cs           turns each app's .exe icon into an image for the page
    Log.cs                 writes %LOCALAPPDATA%\GlassMixer\glassmixer.log
    app.manifest           sharp text on every monitor (per-monitor DPI)
    ui/                    the HTML, CSS and JS you see inside the window, plus fonts/
installer/               install.ps1, uninstall.ps1, Install Glass Mixer.cmd, GlassMixer.iss
tools/                   build-release.ps1, make-icon.ps1, get-fonts.ps1
```

Data flows in a loop:

```
 Windows audio  ──NAudio──▶  AudioService  ──JSON──▶  app.js draws sliders and meters
       ▲                                                        │
       └───────── SetVolume / SetMute ◀──JSON── slider moved ◀──┘
```

## What to learn from each file

Read them in this order. Each one adds one idea.

### 1. `Core/AudioService.cs`: talking to Windows audio

- Windows keeps an **audio session** for every program that plays sound. Each session has its own volume, mute and live **peak level** (how loud it is right now, 0.0 to 1.0).
- `MMDeviceEnumerator` finds your output device. `device.AudioSessionManager.Sessions` lists the sessions on it.
- One program can have several sessions (Chrome has one per process), so they're **grouped by .exe name** into one row.
- Sessions disappear when apps close. Calling a dead session throws a `COMException`, which `Safe(...)` turns into "silent" so the mixer never crashes.
- **Try it:** run the Probe while music plays, then `set <id> 20` and listen.

### 2. `App/MainWindow.xaml`: a window without a title bar

- `WindowChrome` hides the title bar but keeps a real Windows frame. Windows needs that frame to draw the shadow, the rounded corners and the glass.
- The WebView2 has a 6px `Margin`. That strip belongs to the WPF window, so Windows still lets you resize from the edges.

### 3. `App/Native.cs`: P/Invoke

- `[DllImport("dwmapi.dll")]` calls a Windows function directly from C#. That's how you reach anything .NET doesn't wrap.
- `ApplyAcrylic` asks DWM (the part of Windows that draws windows) for acrylic glass behind the whole window. CSS can't do this: `backdrop-filter` only blurs things inside the page, never other programs.
- `BeginSystemMove` tells Windows "the mouse went down on the title bar", so Windows moves the window for us. This is how a page with no title bar can be dragged.

### 4. `App/MainWindow.xaml.cs`: the bridge

- C# to page: `PostWebMessageAsJson(json)`. Page to C#: `chrome.webview.postMessage(obj)`, which arrives in `OnWebMessage`.
- Two timers: **meters** every 33 ms (about 30 frames a second), and a **session refresh** every 1.5 s to notice apps that start or stop.
- `SendState` only sends when something changed, so changes made in the Windows mixer still show up here without flooding the page.

### 5. `App/ui/app.js`: the page

- `host` is `window.chrome.webview` inside the app. In a normal browser it's `createFakeHost()`, so you can work on the design in Chrome or Edge with sample data.
- `touchedAt` stops incoming updates from snapping a slider back while you're dragging it.
- Meters use `Math.sqrt(peak)` (quiet sounds stay visible) and a slow fall-off (no flicker).

### 6. `App/App.xaml.cs`: behaving like a tray app

- A named `Mutex` keeps it to one copy: a second launch exits.
- `ShutdownMode="OnExplicitShutdown"` and the `Closing` handler mean hiding never quits the app. Only **Quit** in the tray does.

## Debugging

- **C# and page errors** go to `%LOCALAPPDATA%\GlassMixer\glassmixer.log`. Open it with:
  ```bash
  notepad %LOCALAPPDATA%\GlassMixer\glassmixer.log
  ```
- **Page DevTools:** in a Debug build, click the window and press **F12**. You get the same Elements and Console tabs as in Edge.
- **UI only:** open `src/GlassMixer.App/ui/index.html` in Edge to work on the HTML and CSS with the fake host.
- **Testing from a packaged app's terminal** (like the Claude desktop app, which is an MSIX package): Windows quietly redirects that terminal's writes to `AppData` and `HKCU` into the package's private copy. That covers everything it starts, so an install from there only exists inside that copy. Run installers through Explorer instead, so they land in your real Windows: `explorer.exe "path\to\Install Glass Mixer.cmd"`.

### 7. Matching the mockup

- `ui/` is a port of the mockup's own HTML, CSS and JS. The fake data was replaced by `state` and `levels` messages from C#.
- **A truly transparent window** (`Native.SetGlass`): `DwmEnableBlurBehindWindow` with an empty region tells Windows to respect the window's transparent pixels. Extending the frame instead (`DwmExtendFrameIntoClientArea`) only works with Windows 11's own Mica or Acrylic backdrops. Without one, Windows paints the frame flat grey, which was the first version's bug.
- **The adjustable blur** (`BackdropBlur.cs`): Windows' own blur strength is fixed, so the app draws the blur itself about 30 times a second.
  1. `SetWindowDisplayAffinity(EXCLUDEFROMCAPTURE)` leaves the mixer out of screen captures.
  2. `BitBlt` copies the screen under the window, which now shows what's behind it.
  3. WPF's `BlurEffect` blurs that copy on the GPU at the slider's radius.

  Side effect: while the blur is on, the mixer doesn't appear in screenshots or screen sharing. At 0px it's off.
- **Smooth 18px corners and the shadow** (`MainWindow.xaml`): the window is a transparent rectangle 12px bigger than the panel. WPF draws a soft `DropShadowEffect` in that margin, and the blur is clipped with an anti-aliased rounded `RectangleGeometry`. The margin is also the resize border. An earlier version clipped the window with `SetWindowRgn`, but regions can't be anti-aliased, so the corners were stepped.
- **Sharp text on every monitor** (`app.manifest`): `PerMonitorV2` DPI awareness. Without it, Windows draws the app at one scale and stretches it on a monitor with different scaling, which blurs the text.
- **Smooth meters** (`app.js`, `animateMeters`): levels arrive about 30 times a second. The page animates toward the latest one at the screen's frame rate, with the mockup's fast rise, slow fall and peak hold.
- **Debugging the blur**: screenshots can't see the window while the blur is on. In a Debug build, the page message `{ type: 'dumpBackdrop' }` saves what WPF draws to `%LOCALAPPDATA%\GlassMixer\backdrop.png`.
- **Balance** (`Core/SessionChannels.cs`): every session has an `IChannelAudioVolume` with one level per channel (0 left, 1 right). It's a documented interface NAudio doesn't wrap, so this file declares the COM interfaces itself. That's a good model for calling any COM API.
- **Per-app output** (`Core/AppOutput.cs`): uses `AudioPolicyConfig`, the private Windows Runtime class behind Settings > Volume mixer > Output device. Apps usually switch when they next start a sound. If a Windows update ever breaks it, the Output menu turns itself off.
- **Sessions from every device** (`AudioService.Refresh`): an app you send to another device stays in the list.
- **Real app icons** (`IconCache.cs`): each .exe's icon becomes a PNG data URL the page shows in an `<img>`.
- **Device dropdown** (`Core/DefaultDevice.cs`): it really switches the Windows default output, through `IPolicyConfig`. That's a private COM interface, and this file shows how to call one from C#.
- **Profiles** are saved mixes. Clicking an empty (dashed) chip saves the current volumes; clicking a saved chip applies them; right-clicking a chip saves over it.

### 8. Polish: behaving like a finished app

- **Remembers where it was** (`WindowSettings.cs`): it saves the bottom-right corner and the full size, because it grows and shrinks around that corner like a tray flyout. It saves 0.6s after you stop moving it, not on every pixel. It ignores a saved spot that's now off-screen (an unplugged monitor).
- **Start with Windows** (`StartWithWindows.cs`): one registry value under `HKCU\...\Run`. Windows passes `--startup`, so the app waits in the tray; `EnsureHandle()` creates the hidden window so the hotkeys work right away.
- **Live tray tooltip**: a 1-second timer builds "Glass Mixer · Headphones 70%" and only updates the tray when the text changes.
- **Pop-in**: the mockup's `pop` animation plays when the window opens, minimizes or expands.
- **Bundled fonts**: the `.woff2` files and their licenses (OFL) ship in `ui/fonts`, so nothing loads from the internet.
- **Single-file release**: one `GlassMixer.exe` with .NET inside. `ExcludeFromSingleFile` keeps `ui/` and `app.ico` as real files, because WebView2 and the tray icon read them from disk.
- **A per-user installer**: no admin rights. The `Uninstall` registry key is what makes an app appear in Settings > Apps.

## Next steps (good exercises)

1. **Faster capture.** `BitBlt` is simple but copies through the CPU. `Windows.Graphics.Capture` keeps frames on the GPU and only sends new ones when the screen changes.
2. **"Now playing" titles**, like the mockup's "Now playing · Kundiman Sessions": the WinRT `GlobalSystemMediaTransportControlsSessionManager` gives the song or video title per app.
3. **Follow the theme live.** Listen for `WM_SETTINGCHANGE` in `WndProc` and call `PrepareGlassWindow` again.
4. **Open on the screen with the tray.** Today it uses the primary screen's work area.
5. **Start with Windows.** Add a value under `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`.
6. **Bundle the fonts.** They load from Google Fonts today, so offline the app falls back to Segoe UI. Put the font files in `ui/fonts` and use `@font-face`.
7. **Installer.** Publish with `dotnet publish -c Release -r win-x64 --self-contained`, then package it with Inno Setup or MSIX.
