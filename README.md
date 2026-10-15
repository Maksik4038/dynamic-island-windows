# Dynamic Island for Windows 11

An iPhone-style Dynamic Island for the Windows desktop: a black pill fused to the top
edge of the screen that springs open on hover and doubles as a status surface for
whatever is happening on the machine.

Built with nothing but the in-box .NET Framework compiler and Win32/GDI+. No installer
framework, no external packages, one ~130 KB executable.

## Get it

**[⬇ Download DynamicIsland.zip](https://github.com/Maksik4038/dynamic-island-windows/releases/latest/download/DynamicIsland.zip)** — ready to run.

Unpack it anywhere and start `DynamicIsland.exe`. The executable is already inside, so no
compiler and no Windows SDK are needed. To have it start with Windows and appear in the
Start Menu:

```powershell
powershell -ExecutionPolicy Bypass -File Install.ps1
```

If it will not start, the bundled diagnostic says why:

```powershell
powershell -ExecutionPolicy Bypass -File Diagnose.ps1
```

Requirements: Windows 11 and the .NET Framework 4.x, which ships with Windows.

![idle](shots/idle-black.png)

## What it shows

| State | Appearance |
|---|---|
| Idle | a plain black pill — nothing on it |
| Track playing | disc + track title, draggable seek bar, tap right side to skip |
| Microphone in use | orange microphone glyph and orange rim |
| Music **and** microphone | the microphone detaches into its own small island on the left |
| Stopwatch / timer running | a single small glyph |
| Timer finished / alarm ringing | the island unfurls downward into a card with controls |
| USB / Bluetooth device connected | card with a device icon, transport badge and battery |
| File downloaded | progress bar, then a green tick in a green circle |
| Bluetooth / Wi-Fi switched | a mark for three seconds |
| Keyboard layout switched | the code: `rus`, `eng`, `ukr` … |
| Application opened | its icon, centred, for three seconds |
| Screen locked | a small padlock beside the island |

<p align="center">
  <img src="shots/mini-player-art.png" width="420" alt="mini player">
  <img src="shots/mic-dot.png" width="420" alt="microphone">
  <img src="shots/mini-plus-mic.png" width="420" alt="music and microphone">
  <img src="shots/act-device-gamepad.png" width="420" alt="device connected">
</p>

More states are in [`shots/`](shots) — one PNG per state, rendered by the app itself.

## Controls

- **Hover** the pill to expand the full panel (waveform, date, volume and brightness).
- **Drag** the two bars to set system volume and screen brightness.
- **Click** the speaker glyph to mute; **scroll** over the island to change volume.
- **Right-click the tray icon** for the resting width, autostart, settings and quit.

Resting width is configurable from a dot (16 px) up to 220 px; below capsule height the
island becomes a true circle.

## Building

Requirements: .NET Framework 4.x (ships with Windows) and the **Windows SDK 10** (needed
only to compile — it supplies `Windows.winmd` for the media API).

```powershell
powershell -ExecutionPolicy Bypass -File Build.ps1
powershell -ExecutionPolicy Bypass -File Build.ps1 -Run
```

`Build.ps1` finds the newest SDK itself. To compile by hand:

```powershell
$winmd = 'C:\Program Files (x86)\Windows Kits\10\UnionMetadata\10.0.28000.0\Windows.winmd'
$wrt   = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\System.Runtime.WindowsRuntime.dll"
$sr    = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\System.Runtime.dll"

csc /target:winexe /out:DynamicIsland.exe `
    /reference:System.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll `
    /reference:System.Core.dll /reference:System.Management.dll `
    /reference:$sr /reference:$winmd /reference:$wrt `
    DynamicIsland.cs Activities.cs ActivityPipe.cs Privacy.cs Media.cs Devices.cs Sensors.cs Settings.cs
```

## Installing

```powershell
powershell -ExecutionPolicy Bypass -File Install.ps1
```

Copies the app to `%LOCALAPPDATA%\DynamicIsland`, creates Start Menu and Desktop
shortcuts and registers autostart through the HKCU `Run` key — no administrator rights.
To remove everything: `Install.ps1 -Uninstall`.

## Live activities from the command line

Any script can drive the island. If it is already running, the second instance forwards
the command and exits.

```powershell
$DI = "$env:LOCALAPPDATA\DynamicIsland\DynamicIsland.exe"

& $DI stopwatch start          # also: pause, reset, stop
& $DI timer 10m "Focus"        # also: 90s, 1h30m; pause, reset, +5m, dismiss
& $DI alarm 07:30              # snooze = +10 min, dismiss
& $DI notify "Build done" "3 tests failed"
& $DI lang rus
& $DI radio bluetooth on
& $DI download "setup.exe" 0.42
& $DI downloaded "setup.exe"
& $DI app Photoshop
```

Full command reference: [COMMANDS.md](COMMANDS.md).

## Diagnostics

```powershell
& $DI --selftest       # volume, brightness, battery, DPI, taskbar geometry
& $DI --privacy        # watch microphone/camera capture live
& $DI --probe-media    # is the media session API reachable?
& $DI --watch          # what the language / radio / window watchers currently see
& $DI --snapshot DIR   # render every state to PNG (used for the shots above)
```

## If it does not start

Run the diagnostic first — it checks everything that commonly goes wrong and prints the
result of the built-in self test:

```powershell
powershell -ExecutionPolicy Bypass -File Diagnose.ps1
```

The most frequent causes, in order:

| Symptom | Cause | Fix |
|---|---|---|
| `DynamicIsland.exe` is not there | build output is not stored in the repository | `Build.ps1` |
| Build stops at `Windows.winmd not found` | Windows SDK 10 is missing | install it, or grab a Release exe |
| Nothing happens at all, no window | .NET Framework 4.x missing | install .NET Framework 4.8 |
| Window flashes and vanishes | a startup exception | there is now a message box, and `island-error.log` next to the exe |
| It runs, but no island on screen | the app started fine — look for the tray icon | the pill is at the very top centre, y = 0 |
| Nothing after downloading from GitHub | the file is flagged as from the internet | `Unblock-File .\DynamicIsland.exe` |

Startup failures used to be completely silent: the executable is a GUI-subsystem build, so
an exception produced no window, no console output and no clue. The entry point now reports
any fatal exception in a message box **and** appends it to `island-error.log` beside the exe.

## How it works

A layered window (`WS_EX_LAYERED` + `UpdateLayeredWindow`) over a persistent 32bpp DIB
back-buffer, so the pill is genuinely antialiased per-pixel rather than a clipped
rectangle. Motion comes from a critically damped spring integrated in 2 ms substeps, so
it feels the same at 60 Hz and 144 Hz.

Everything is read through plain Win32, COM and the WinRT projection:

| Feature | Source |
|---|---|
| Volume | CoreAudio `IAudioEndpointVolume` |
| Screen brightness | Dxva2 `Get/SetMonitorBrightness` |
| Now playing + artwork | WinRT `GlobalSystemMediaTransportControlsSessionManager` |
| Microphone in use | CoreAudio capture sessions |
| USB / Bluetooth devices | WMI `Win32_DeviceChangeEvent` + `Win32_PnPEntity` |
| Bluetooth / Wi-Fi state | WinRT `Windows.Devices.Radios` |
| Keyboard layout | `GetKeyboardLayout` of the foreground thread |
| Downloads | file watching (see limitations) |
| Lock screen | `OpenInputDesktop` / `SwitchDesktop` |

The media path is validated in a **child process** before the widget uses it: a wrong
call against a WinRT vtable would otherwise take the whole app down.

## Если не запускается (по-русски)

Запустите диагностику — она проверит всё, что обычно мешает, и напечатает результат
встроенного самотеста:

```powershell
powershell -ExecutionPolicy Bypass -File Diagnose.ps1
```

Частые причины:

| Что видите | В чём дело | Что делать |
|---|---|---|
| Файла `DynamicIsland.exe` нет | собранный exe не хранится в репозитории | запустить `Build.ps1` |
| Сборка падает: `Windows.winmd not found` | не установлен Windows SDK 10 | установить SDK или взять готовый exe из Releases |
| Ничего не происходит, окна нет | не установлен .NET Framework 4.x | поставить .NET Framework 4.8 |
| Окно мелькнуло и исчезло | исключение при запуске | теперь появится окно с ошибкой и файл `island-error.log` рядом с exe |
| Работает, но острова не видно | приложение запустилось нормально | ищите значок в трее; остров в самом верху по центру, y = 0 |
| После скачивания с GitHub ничего | файл помечен как из интернета | `Unblock-File .\DynamicIsland.exe` |

Раньше сбой при запуске был **полностью молчаливым**: exe собран как GUI-приложение, поэтому
исключение не показывало ни окна, ни вывода в консоль. Теперь любая фатальная ошибка
попадает в окно сообщения и в `island-error.log` рядом с exe.

## If Windows refuses to run it

A downloaded executable with no publisher signature can be stopped by Windows
security. The message usually says the publisher is unknown.

Run the checker and it will say which protection is responsible:

```powershell
powershell -ExecutionPolicy Bypass -File CheckBlocked.ps1
```

| What section 1 of the report says | What it means | What to do |
|---|---|---|
| `[ OK ] OFF` | Smart App Control is not the problem | look at sections 3 and 4 of the report |
| `[BLOCK] ON in enforcing mode` | **Smart App Control** blocks every unsigned program. There is no per-app exception | build from source, or turn Smart App Control off (see below) |
| `[ ? ] ON in evaluation mode` | it blocks only what it considers unsafe | `Unblock-File .\DynamicIsland.exe` and try again |

**Building from source is the recommended workaround.** Windows treats freshly
compiled code far more leniently than a file downloaded from the internet, and it
needs no administrator rights:

```powershell
powershell -ExecutionPolicy Bypass -File Build.ps1
```

**Turning Smart App Control off** also works, but read the warning: Windows does
not let you turn it back on without reinstalling the system.

*Settings → Privacy and security → Windows Security → App and browser control →
Smart App Control.*

The only way to ship one file that starts silently for everyone is to **sign it with
a publisher certificate**. That costs money and requires an identity check; a
self-signed certificate does not help, because nothing trusts it by default.

## Если Windows не даёт запустить

Скачанный exe без подписи издателя может быть остановлен защитой Windows.
Обычно сообщение говорит, что издатель неизвестен.

Запустите проверку — она скажет, какая именно защита мешает:

```powershell
powershell -ExecutionPolicy Bypass -File CheckBlocked.ps1
```

| Что в пункте 1 отчёта | Что это значит | Что делать |
|---|---|---|
| `[ OK ] OFF` | дело не в «Интеллектуальном управлении» | смотрите пункты 3 и 4 отчёта |
| `[BLOCK] ON in enforcing mode` | **Интеллектуальное управление** блокирует любую неподписанную программу, исключений для отдельных приложений нет | собрать из исходников или выключить защиту (см. ниже) |
| `[ ? ] ON in evaluation mode` | блокирует только то, что считает опасным | `Unblock-File .\DynamicIsland.exe` и повторить запуск |

**Сборка из исходников — рекомендуемый обход.** Windows относится к только что
собранному коду намного мягче, чем к скачанному из интернета, и это не требует
прав администратора:

```powershell
powershell -ExecutionPolicy Bypass -File Build.ps1
```

**Выключение «Интеллектуального управления»** тоже помогает, но прочтите
предупреждение: обратно включить его нельзя без переустановки системы.

*Параметры → Конфиденциальность и защита → Безопасность Windows → Управление
приложениями и браузером → Интеллектуальное управление.*

Единственный способ поставлять один файл, который запускается у всех без
предупреждений — **подписать его сертификатом издателя**. Это стоит денег и
требует проверки личности; самоподписанный сертификат не поможет, потому что
ему по умолчанию никто не доверяет.

## Status colours and fullscreen

Instead of drawing a separate icon for every event, the island tints a thin strip along
its top edge:

| Event | Colour |
|---|---|
| Charging | green |
| Microphone in use | orange |
| Music playing | blue |
| Keyboard layout switched | white |
| Bluetooth / Wi-Fi | red |
| Download | cyan |
| Device, notification, timer, alarm, app launch | purple |

Several events at once are drawn side by side, so the island reads as "part green, part
orange". This also means the microphone stays visible while charging, without either one
hiding the other.

When a window fills the whole working area - a browser in F11, a game, a video player, or
any window using the entire screen - the island collapses to a **flat capsule** at the very
top edge, the same length as always but only 15 dp tall, showing the status colours and
nothing else. The taskbar being visible does not change this. Use `& $DI --fullscreen` to
see exactly what the detection decides for the window you have in front.

## Honest limitations

These are real restrictions of the platform, not unfinished work:

- **Browser download progress cannot be read.** Chrome and Edge keep transfer state in
  their own profile databases; there is no public API for another process's download.
  The island detects a finished file in `Downloads`, and shows a progress bar for partial
  files when the browser leaves them visible.
- **Download percentage is exact only when reported by the caller** (`download "name" 0.42`).
  Otherwise an indeterminate bar is shown rather than an invented number.
- **Application icons are best-effort.** `WM_GETICON` often returns nothing and
  `ExtractIconEx` can fail on elevated processes; a coloured monogram tile is used as the
  fallback.
- **Camera activity is not implemented.** The ConsentStore registry only records apps that
  go through the capability broker (verified: it does not update for the Windows Camera
  app), `AppCapability` requires package identity, and device state cannot be distinguished
  from a permissions failure. The microphone *is* detected reliably, through CoreAudio.
- **Windows' own Clock app alarms/timers cannot be imported** — the app keeps them in an
  internal store with no accessible file (`LocalState` contains UI state only).

## License

MIT — see [LICENSE](LICENSE).
