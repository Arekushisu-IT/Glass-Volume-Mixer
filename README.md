# Glass Mixer

A desktop volume mixer for Windows 10 and 11 that controls every app's sound
separately, from a see-through glass panel with an adjustable blur. It lives at
the top, left or right edge of your screen and tucks away into a small pill when
you're not using it. It starts with Windows.
Built with **C# (.NET 10) + WPF + WebView2 + NAudio**.

The design follows the interactive mockup in [`docs/mockup.html`](docs/mockup.html).

## Features

- A volume, mute, live level meter, **balance** and **output device** for every app playing sound
- Master volume, and switching the Windows default output device
- **Lives at a screen edge:** drag it along the top, or to the left or right side. Small and wide at the top (about 640 × 320), narrow on the sides
- **The pill:** when your mouse leaves, the mixer tucks away into a small pill at its edge, showing what's playing, live level bars and the volume. For a moment it grows to show what changed
- **Now Playing:** the song or video, cover art, progress, and play/pause, next and previous, for any app that uses the Windows media controls
- **Microphone in use:** which app is recording, an orange dot on the pill, the mic level and a mute button
- **Volume keys show the pill:** the keyboard's volume and mute keys make it show the new level
- **Profiles:** saved mixes you apply with one click, and **automatic profiles** (Meeting during a call, Gaming while a game runs, Focus in work hours)
- **List** and **Mixer** (vertical faders) layouts
- Glass with adjustable **blur (0–40px)** and **transparency**, smooth 18px corners and a soft shadow
- A tray icon with a live tooltip, **Start with Windows**, and global hotkeys

## Install

Download `GlassMixer-<version>-win-x64.zip` from the Releases page, unzip it, and double-click
**Install Glass Mixer.cmd**.
- It installs for your user only, with no admin rights needed.
- It adds a **desktop icon** and a Start Menu shortcut, and appears in **Settings > Apps**, where you can uninstall it.
- It turns on **Start with Windows**. Turn it off from the tray icon's right-click menu.
- It needs the Microsoft Edge WebView2 Runtime, which ships with Windows 11.

## Use it

- **Drag the header** to move the mixer. Let go and it snaps to the nearest edge (top, left or right) of that screen, at the spot along it where you let go. It remembers the spot.
- **Move your mouse away** and it tucks into the pill. **Rest on the pill**, click it, or push the mouse against that screen edge, to bring it back. Scroll over the pill to change the volume without opening it.
- **Drag the pill** to move it along its edge, or to another edge. The mixer opens wherever the pill is: they share one spot.
- **Ctrl+Alt+V**, the **tray icon** and the **tuck button** (the arrow in the header) open or tuck it. **Esc** tucks it. **Ctrl+Alt+K** mutes the microphone.
- The appearance settings (the sliders icon) have **Tuck away when the mouse leaves** (turn it off to keep the mixer open until you tuck it) and **Show activity on the pill**.
- **Automatic profiles** need a saved mix: click an empty (dashed) Meeting, Gaming or Focus chip to save the current mix, then tick its rule. A call means "an app is using the microphone". A game means a full-screen app that isn't a browser or video player.
- The pill steps out of the way while a full-screen game or video is in front.
- Drag a slider, or scroll the mouse wheel over a row, to change that app's real volume. ▾ on an app opens its output device and balance.
- Right-click the tray icon for **Start with Windows** and **Quit**. After Quit, open it from the Start menu or the desktop icon.

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
    MainWindow.xaml(.cs)   the glass window at a screen edge (panel or pill), WebView2, the message bridge
    DockHint.cs            the glowing "Release to place it here" line while you drag
    MediaWatcher.cs        Now Playing, from the Windows media controls
    FullScreen.cs          is a game or full-screen video in front?
    GlassBackdrop.cs       the adjustable blur behind the window
    BackdropProbe.cs       measures the brightness behind the mixer, for readable text
    Native.cs              Windows API calls: transparency, hotkeys, window drag
    TrayIcon.cs            the tray icon, its tooltip and menu
    StartWithWindows.cs    the "Start with Windows" setting
    WindowSettings.cs      remembers the edge, the spot along it, and the screen
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
- The WebView2 has a 20px `Margin`: transparent room around the panel for its soft shadow. The window has no resize border, because its size comes from the screen edge it lives on.

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
- **The adjustable blur** (`GlassBackdrop.cs`): drawn by Windows itself, with `Windows.UI.Composition`, the layer the Start menu and taskbar use.
  1. `CreateDesktopWindowTarget` adds a composition "visual" (a rectangle) to the window, under the web page.
  2. It's painted with a **host backdrop brush**: a live view of whatever is behind the window.
  3. A Direct2D **Gaussian blur** effect sits on that brush; the slider sets its strength.
  4. A rounded **geometric clip** gives it the panel's anti-aliased 18px corners.

  Everything runs on the GPU inside Windows, so the mixer appears normally in screenshots and screen recordings.
  - **The first version** copied the screen 30 times a second (`BitBlt`) and blurred the copy. To copy what was behind it, the app had to hide itself from all screen capture, so it vanished from screenshots and broke screen recorders.
  - **The hand-built COM object** at the bottom of that file: the Compositor asks an effect for `IGraphicsEffectD2D1Interop`. The Win2D library normally supplies it; here it's a small table of function pointers.
- **Smooth 18px corners and the shadow** (`MainWindow.xaml`): the window is a transparent rectangle 20px bigger than the panel on every side. WPF draws a soft `DropShadowEffect` in that margin, and the margin is also the resize border. Two lessons from earlier versions:
  - Clipping the window with `SetWindowRgn` gives stepped corners, because regions can't be anti-aliased.
  - A shadow needs room. With only 12px of margin it was cut off in a straight line, which looked like a sharp corner beside the smooth one.
- **Readable on any background** (`BackdropProbe.cs`, `tintNeeded` in `app.js`): twice a second the app reads four one-pixel lines at the window's edge and averages their brightness. The page then keeps the glass tint at least as strong as its text needs: nothing extra over a dark background, more over a bright one.
- **Sharp text on every monitor** (`app.manifest`): `PerMonitorV2` DPI awareness. Without it, Windows draws the app at one scale and stretches it on a monitor with different scaling, which blurs the text.
- **Smooth meters** (`app.js`, `animateMeters`): levels arrive about 30 times a second. The page animates toward the latest one at the screen's frame rate, with the mockup's fast rise, slow fall and peak hold.
- **Driving the page from a script** (how the screenshots in testing were made): start the app with the environment variable `WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS=--remote-debugging-port=9229`. Any DevTools-protocol client can then run JavaScript in the page, for example to click buttons or change settings.
- **Balance** (`Core/SessionChannels.cs`): every session has an `IChannelAudioVolume` with one level per channel (0 left, 1 right). It's a documented interface NAudio doesn't wrap, so this file declares the COM interfaces itself. That's a good model for calling any COM API.
- **Per-app output** (`Core/AppOutput.cs`): uses `AudioPolicyConfig`, the private Windows Runtime class behind Settings > Volume mixer > Output device. Apps usually switch when they next start a sound. If a Windows update ever breaks it, the Output menu turns itself off.
- **Sessions from every device** (`AudioService.Refresh`): an app you send to another device stays in the list.
- **Real app icons** (`IconCache.cs`): each .exe's icon becomes a PNG data URL the page shows in an `<img>`.
- **Device dropdown** (`Core/DefaultDevice.cs`): it really switches the Windows default output, through `IPolicyConfig`. That's a private COM interface, and this file shows how to call one from C#.
- **Profiles** are saved mixes. Clicking an empty (dashed) chip saves the current volumes; clicking a saved chip applies them; right-clicking a chip saves over it.

### 8. Polish: behaving like a finished app

- **Start with Windows** (`StartWithWindows.cs`): one registry value under `HKCU\...\Run`. Windows passes `--startup`, so the app starts as the pill instead of the open mixer.
- **Live tray tooltip**: a 1-second timer builds "Glass Mixer · Headphones 70%" and only updates the tray when the text changes.
- **Bundled fonts**: the `.woff2` files and their licenses (OFL) ship in `ui/fonts`, so nothing loads from the internet.
- **Single-file release**: one `GlassMixer.exe` with .NET inside. `ExcludeFromSingleFile` keeps `ui/` and `app.ico` as real files, because WebView2 and the tray icon read them from disk.
- **A per-user installer**: no admin rights. The `Uninstall` registry key is what makes an app appear in Settings > Apps.

### 9. Version 1.1: living at a screen edge (macOS-style features)

- **One window, two shapes** (`MainWindow.xaml.cs`): the panel and the pill are the same window and the same page. Swapping takes one round trip, so nothing ever shows at the wrong size: C# says `shape`, the page clears what it shows and answers `shapeReady`, C# resizes and moves the window, then says `placed` and the page shows the new shape.
- **Physical pixels** (`Native.cs`, "screen edges"): placement uses `MonitorFromPoint`, `GetMonitorInfo`, `GetDpiForMonitor` and `SetWindowPos` instead of WPF's `Left`/`Top`, because WPF's scaled units don't line up across two monitors with different scaling (100% and 125%, for example).
- **Snapping to an edge**: the header (or the pill) still starts Windows' own move loop (`BeginSystemMove`). `WM_MOVING` arrives while you drag; its `lParam` is the rectangle the window is *about to* move to, which is what the drop hint uses (`GetWindowRect` still has the old one at that moment). When the loop returns, the spot is read from where the window really is: 1.1.0 used the last hint instead, which could be the starting spot, so the mixer jumped back to the middle of the edge.
- **Click-through hint** (`DockHint.cs`): a layered WPF window with `WS_EX_TRANSPARENT`, so the mouse passes straight through it.
- **Tucking**: a 60 ms timer compares the cursor (`GetCursorPos`) with the window. 650 ms outside the panel tucks it; 280 ms on the pill, or a touch on the screen edge, opens it. An open drop-down list (`busy`) or a held mouse button stops it from tucking.
- **The window fits its content**: the page reports its natural height (`content`), so a short mixer never shows empty glass. Measured from an inner wrapper, because a scroll box's `scrollHeight` never shrinks below its own height.
- **Volume keys** (`AudioService.MasterChanged`): `AudioEndpointVolume.OnVolumeNotification` is Windows calling us back on every change to the volume, from anyone. It arrives on a background thread, so `Dispatcher.BeginInvoke` hops to the UI thread.
- **Now Playing** (`MediaWatcher.cs`): `GlobalSystemMediaTransportControlsSessionManager` lists every app that publishes media to Windows (the same data as the Windows volume flyout). Title and cover are read once per song; the position is only sent now and then, and the page counts the seconds in between.
- **Microphone in use** (`AudioService.RefreshMic`): recording apps have an active session on a capture device, just as playing apps have one on an output device.
- **Games** (`FullScreen.cs`): `SHQueryUserNotificationState` is what Windows' own "don't disturb me while gaming" uses.
- **Automatic profiles** (`evaluateAuto` in `app.js`): the first matching rule wins; a snapshot of your mix is taken when a rule starts and put back when it ends. Picking a profile yourself overrides the rule until its condition changes.
- **The pill's queue** (`notifyIsland`): one message at a time; volume changes update the message on screen instead of queueing.

## Next steps (good exercises)

1. **A shadow drawn by the Compositor too.** `Compositor.CreateDropShadow()` on the blur visual would replace the WPF shadow layer.
2. **Animate the pill's size.** The window jumps to the grown size; a composition animation on the blur visual could make the growth smooth like the mockup.
3. **Code signing.** A signed installer removes the "Windows protected your PC" warning.
4. **A classic `setup.exe`.** `installer/GlassMixer.iss` is ready for Inno Setup.
