<div align="center">

# 🖥️ Ampz Desktop Booster

**A keyboard-first productivity cockpit built around your projects and tasks.**
Your alternative to a wall of monitors — go *deep* on one screen instead of *wide* across glass (and save your neck).
Every virtual desktop becomes a **project** with its own **tasks**, *variables* and *notes* — all one numpad keystroke away, without ever touching the mouse.

[![.NET 10](https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![WPF](https://img.shields.io/badge/UI-WPF-0078D6?logo=windows&logoColor=white)](https://learn.microsoft.com/dotnet/desktop/wpf/)
[![Platform](https://img.shields.io/badge/platform-Windows%2010%20%2F%2011-0078D6?logo=windows11&logoColor=white)](#)
[![Arch](https://img.shields.io/badge/arch-x64-lightgrey)](#)
[![License](https://img.shields.io/badge/license-Proprietary-red)](LICENSE)

</div>

---

## ✨ What is this?

**An alternative to a wall of monitors.** You know the setup: three, four, five screens fanned around you — and a neck that aches from constantly craning left and right, hunting for the one piece of data you need. More monitors don't give you more focus; they give you more *neck strain*.

Ampz Desktop Booster flips the model: instead of spreading **wide** across glass, you go **deep** on a single screen. Each workspace is one numpad keystroke away, dead ahead — eyes forward, head still. **One screen, infinite depth.**

But the screen is just the vehicle. **The focus is productivity — and productivity here means projects and tasks.** Each virtual desktop *is* a project: it carries the task you're working on, the paths and URLs you need, and the notes you've taken — so switching desktops isn't switching windows, it's switching *context*. You land on a desktop and everything that project needs is already there, behind keys you know by feel.

So while Windows virtual desktops are powerful but *forgettable* — nameless, ordered by position, easy to lose track of — **Ampz Desktop Booster** turns them into a project-and-task workspace:

- 📋 **Projects** — every desktop is a project with a stable identity you reach by **name**, not by position.
- ✅ **Tasks** — pin the live task you're on (from **Vikunja / JIRA / Trello**) to its desktop; it shows in the bar and follows the context.
- 🔗 **Context** — **variables** (paths & URLs) and **notes** resolve to the active project automatically, so your tools are always at hand.
- 🛡️ **Order** — a **window governor** keeps the right apps on the right desktops, so a project's space stays a project's space.
- ⌨️ **Flow** — it's all driven from the **numpad**, for people who don't want to leave the keyboard mid-thought.

It's the modern **WPF / .NET 10** rewrite of a battle-tested AutoHotkey v2 script. The behavior is the source of truth; the codebase is the polished, maintainable evolution of it.

> 📸 *Screenshots coming soon — drop your captures in a `docs/` folder and link them here.*

---

## ⚠️ The trade-off — you sacrifice a numpad

**This is the price of admission. Read it before you install.**

Ampz Desktop Booster **hijacks your numpad.** To make the shortcuts rock-solid, it:

- **Pins NumLock to OFF permanently** — it's suppressed at the hook level, no polling, no fighting.
- **Intercepts every numpad key** and decodes it by *scancode*, so the bindings work regardless of NumLock state.

The consequence is blunt: **while the app runs, your numpad no longer types numbers.** It becomes a dedicated control pad for your desktops — that's the whole point, but it's a one-way street.

> 💡 **Recommendation:** dedicate a numpad to it. Ideally a **separate USB numpad** (or a full keyboard whose numpad you don't use for typing) that you hand over to the app entirely. If your *only* numpad is the one you type numbers with, this tool will fight your muscle memory. A cheap external numpad is the sweet spot — sacrifice it to the desktop gods and never look back. 🎹

---

## 🚀 Features

| | |
|---|---|
| 🎯 **Navigation by name** | `Win+Numpad7` always jumps to **MAIN**, even if you reorder your desktops. Identity follows the name, never the index. |
| 📁 **Projects per desktop** | Assign a project to a desktop and it carries its own variables and notes. Three layers: live session, suggestions, and a durable catalog. |
| ✅ **Task integration** | Pin a live task from **Vikunja**, **JIRA** or **Trello** to a desktop. Multi-account, parallel fetch, shown in the bar widget. |
| 🔗 **Variables (paths & URLs)** | A filterable pool per project + a shared global pool. Grouped by type (folders / URLs), one default per pool, opened to the right monitor. |
| 📝 **Dual notes** | Project/global notes **and** notes linked to the active Explorer folder — annotate a repo and the notes follow the folder, even if you move it. |
| 🛡️ **Window governor** | **Pins** keep an app on its desktop; **restrictions** turn a desktop into a whitelist-only zone. Stray windows are moved automatically. |
| 📊 **System widget** | Live CPU / RAM / battery / network in a real **AppBar** that pushes other windows aside (maximizing never covers it). |
| 🔔 **Per-desktop attention** | External signals (via Named Pipe) raise a quiet, per-desktop alert with a widget dot, toast and sound. |
| 🧰 **Developer quick-actions** | Open terminal here, "Open with", environment variables, a Docker panel, downloads, refresh-rate (Hz) switcher, and more. |
| ⌨️ **100% keyboard** | NumLock is permanently suppressed; the numpad is decoded by scancode so it's rock-solid regardless of NumLock state. |

---

## 🎹 Keyboard map

| Shortcut | Action |
|---|---|
| `Win + Numpad 7 / 8 / 9` | Go to **MAIN** / **MAILS** / **MISCS** |
| `Win + Numpad 1..6` | Go to **DESK +1 … +6** |
| `Win + Numpad + / −` | Cycle between the DESK+ that **have an active project** (wrap-around) |
| `Win + Shift + (navigation)` | Send the active window to that desktop **and follow it** |
| `Win + NumpadEnter` | Set the project for the current desktop (DESK +N only) |
| `Numpad 5` *(no Win)* | Open the **Desk Picker** (jump to a session project) |
| `Win + Numpad *` | **Variables** (Paths Manager) — re-press fires the default |
| `Win + Numpad /` | **Notes** (project/global **+** active-folder notes) |
| `Win + Numpad .` *(Del)* | **Send-window picker** — choose a target desktop (shows its project + task) |
| `Win + NumpadInsert` | **Task picker** — assign a task; re-press **unassigns** it (toggle) |
| `Win + F2` | "Open with" (over the active Explorer path) |
| `Win + F3` | Environment variables |
| `Win + F5` | Docker panel |
| `Win + F6` | Toggle pin on the active window |
| `Win + F7` | Pin Manager |
| `Win + F8` | Desktop restrictions |
| `Win + F9` | Whitelist the active app on the current desktop |
| `Win + F11` | Open Downloads |
| `Win + F12` | Refresh-rate (Hz) picker |
| `` Win + ` `` | Open a terminal in the current Explorer path |
| `Win + D` | Intercepted — a custom "show desktop" that minimizes everything **except the bar** |

---

## 🧠 Core concepts

A few ideas make the whole thing click:

- **Identity by name, not index.** Desktops are matched by a case-insensitive name fragment. Move them around all you want — `Win+Numpad7` still lands on MAIN.
- **The three project layers.** *Session* (what's on each desk today, ephemeral) → *Suggestions* (last assignment, to pre-fill the setter) → *Catalog* (durable history, paths & notes). The session is **never** auto-filled from disk at startup — seeing yesterday's unconfirmed projects would be confusing.
- **Dual scope.** On a `DESK +N` with an active project, variables and notes resolve to **that project's** pool. Anywhere else (MAIN/MAILS/MISCS, or an empty DESK+) they fall back to a **shared global** pool.
- **Folder-linked notes.** Notes are keyed by the *folder name*, not its full path — so moving a repo from `Desktop` to `D:\` keeps its notes.
- **The window governor.** Listens for window-show events and desktop changes; a pinned app that appears off its desk is moved back, and a restricted desk only admits its whitelist.

---

## 🛠️ Getting started

### Requirements

- **Windows 10 / 11** (x64)
- **.NET 10 SDK** (`net10.0-windows`)
- A 64-bit machine — the bundled `VirtualDesktopAccessor.dll` is x64-only
- **A numpad you can sacrifice** — see [The trade-off](#️-the-trade-off--you-sacrifice-a-numpad). A dedicated/external numpad is strongly recommended; the app takes it over completely (NumLock stays OFF).

### Build & run

```powershell
# Build (x64 is set by config — no need for -p:Platform)
dotnet build AmpzDesktopBooster.csproj

# Run
dotnet run --project AmpzDesktopBooster.csproj
```

The app is **single-instance** (global mutex). If one is already running, the second copy warns and exits — kill the previous instance before relaunching, or you'll have two keyboard hooks fighting each other.

> The `.slnx` solution also opens directly in Visual Studio.

---

## 💾 Where your data lives

All user data lives in **`%APPDATA%\AmpzDesktopBooster\`** — never next to the executable, so the app stays portable and the binary immutable.

| File | Format | Contents |
|---|---|---|
| `desk_project_data.json` | JSON | Durable catalog: history, notes, paths, shared notes/paths, **folder notes** |
| `settings.ini` | INI | Project suggestions, pins, restrictions, whitelists |
| `desktops.json` | JSON | Managed desktop list + auto-create flag |
| `apps.json` | JSON | User apps for "Open with" |
| `tasks.json` | JSON | Task accounts (Vikunja / JIRA / Trello) |
| `widgets.json` | JSON | Which bar widgets are enabled |
| `ampz-crash.log` | text | Unhandled-exception log (next to the **exe**) |

Every config follows the same resilient pattern: load with a try/catch fallback to defaults, save silently — **a corrupt file or a failed disk write never takes the app down.**

---

## 🏗️ Architecture

Strict layer separation — each piece knows only what it must:

| Folder | Responsibility |
|---|---|
| `Desktops/` | Core: virtual desktops, projects, pins, restrictions, window governance |
| `Hotkeys/` | Low-level keyboard capture and routing of shortcuts to actions |
| `Interop/` | All native P/Invoke: the DLL, hooks (`WH_KEYBOARD_LL`, WinEvent), window methods |
| `Persistence/` | Data paths, custom INI reader, project catalog model |
| `Apps/` | External apps: detection, "Open with", Docker, quick actions, Explorer context |
| `Services/` | AppBar, tray, autostart, system monitor, toasts, tasks, attention, icons |
| `Providers/` | AI-provider logos (embedded resources) |
| *root* | The WPF windows (`*.xaml`) and `App` |

Highlights worth knowing:
- The keyboard hook lives **on the UI thread on purpose** — that's where WPF pumps the messages `WH_KEYBOARD_LL` and the native DLL need.
- Hook callbacks are never blocked: real work is deferred to the `Dispatcher`.
- `DesktopService` is the only layer that touches the native desktop P/Invoke; `SystemMonitor` knows nothing about WPF; `DesktopService` doesn't know about persistence (the project lookup is injected).

### Built with

- **.NET 10** · **WPF** (UI) · **WinForms** (tray icon only)
- **[NAudio](https://github.com/naudio/NAudio)** — attention-sound playback with real volume control
- **VirtualDesktopAccessor.dll** — third-party native library for the undocumented Windows virtual-desktop API

---

## 📜 License

**Proprietary — personal use only.** See [LICENSE](LICENSE).

You may download and use this software for personal, non-commercial purposes. You may **not** sell it, redistribute it, or distribute modified versions. All rights not expressly granted are reserved by the author.

*This is intentionally **not** an open-source license: the source is public so you can read and use it, but the rights stay reserved.*

---

## 🙏 Acknowledgements

- The original **AutoHotkey v2** script that defined the behavior this app faithfully modernizes.
- **VirtualDesktopAccessor** — for exposing the virtual-desktop API Microsoft never documented.

<div align="center">
<sub>Built for developers who keep their hands on the keyboard. 🎹</sub>
</div>
