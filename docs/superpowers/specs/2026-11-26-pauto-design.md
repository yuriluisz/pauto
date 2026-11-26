# pauto — Design Spec

**Date:** 2026-11-26  
**Repository:** `pauto`  
**Target OS:** Windows 10 / 11 (built-in .NET Framework 4.x + WinRT SMTC)

## 1. Goal

Automatically pause **Spotifast** whenever a media/video player starts playing in any desktop web browser on Windows, and automatically resume Spotifast once all browser media players pause, stop, or close — **only** when `pauto` was the one that paused Spotifast.

## 2. Constraints & Design Principles

- **Zero external runtime dependencies:** Compiles directly with Windows' built-in C# compiler (`C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe`) referencing Windows' built-in `.NET Framework 4.x` assemblies and WinRT metadata (`C:\Windows\System32\WinMetadata\Windows.Media.winmd` and `Windows.Foundation.winmd`).
- **No browser extensions & no Spotify API / OAuth:** Uses Windows System Media Transport Controls (**SMTC**) natively.
- **Event-driven & minimal footprint:** Subscribes to SMTC `SessionsChanged` and per-session `PlaybackInfoChanged` events (no tight polling loops), running silently as a WinExe with a system tray icon (`NotifyIcon`).
- **Single instance:** Guarded by a per-user `Mutex` (`Local\pauto-single-instance`) so launching `pauto.exe` twice never spawns conflicting monitors.

## 3. Architecture & File Layout

```text
pauto/
├── src/
│   ├── AutoPauseLogic.cs   # Pure, dependency-free classifier + state machine
│   └── Program.cs          # WinRT SMTC monitor, WinForms tray app, startup toggle, entrypoint
├── tests/
│   └── AutoPauseLogicTests.cs  # Standalone assertion-based unit test runner (compiled with csc.exe)
├── build.ps1               # Compiles dist/pauto.exe and compiles/runs unit tests
├── .gitignore              # Ignores build output (dist/, *.exe, *.pdb)
└── README.md               # Quick build, run, and usage instructions
```

### Component Boundaries

1. **`SessionClassifier` (`src/AutoPauseLogic.cs`)**
   - Pure static helper classifying a Windows SMTC `SourceAppUserModelId` into `SessionKind` (`Spotifast`, `Browser`, or `Other`).
   - Case-insensitive substring matching:
     - `Spotifast`: matches `spotifast`.
     - `Browser`: matches `chrome`, `msedge`, `edge`, `brave`, `opera`, `firefox`, `vivaldi`, `zen`, `arc`, `chromium`, `waterfox`, `floorp`, `thorium`, `librewolf`, `308046b0af4a39cb` (Mozilla Firefox default Windows AUMID hash).
     - `Other`: everything else (ignored by auto-pause triggers).

2. **`AutoPauseController` (`src/AutoPauseLogic.cs`)**
   - Pure state machine with no WinRT, timer, or UI dependencies.
   - Public state:
     - `Enabled` (`bool`, default `true`)
     - `PausedByUs` (`bool`, default `false`)
     - `ResumePending` (`bool`, default `false`)
   - Methods:
     - `Update(bool anyBrowserPlaying, bool spotifastPlaying, bool spotifastExists)` → returns `PlaybackAction` (`None`, `PauseSpotifast`, `ScheduleResume`, `CancelPendingResume`). Note: `PauseSpotifast` also implies cancelling any active resume timer in the caller.
     - `OnResumeTimerElapsed(bool anyBrowserPlaying, bool spotifastPlaying, bool spotifastExists)` → returns `PlaybackAction` (`None` or `ResumeSpotifast`).
     - `NotifyPauseSucceeded()` — sets `PausedByUs = true` and `awaitingPauseConfirmation = true` (cleared once SMTC reports `!spotifastPlaying`, so transient pre-pause SMTC events aren't mistaken for a manual user resume).
     - `NotifyPauseFailed()` — keeps `PausedByUs = false` and clears `awaitingPauseConfirmation = false`.
     - `NotifyResumeCompleted()` — clears `PausedByUs = false`, `ResumePending = false`, and `awaitingPauseConfirmation = false`.
     - `SetEnabled(bool enabled)` — returns `PlaybackAction` (`CancelPendingResume` if a resume timer was armed, else `None`), clears `PausedByUs`, `ResumePending`, and `awaitingPauseConfirmation` when disabled so re-enabling starts cleanly without surprising playback actions.
   - Transition rules:
     - **If `!Enabled`:** update `wasBrowserPlaying = anyBrowserPlaying`, return `None`.
     - **Confirmation check:** if `awaitingPauseConfirmation && !spotifastPlaying`, set `awaitingPauseConfirmation = false`.
     - **Rising edge (`!wasBrowserPlaying && anyBrowserPlaying`):**
       - Remember `wasPending = ResumePending`; clear `ResumePending = false`.
       - If `spotifastExists && spotifastPlaying`, return `PauseSpotifast` (caller cancels any timer and pauses Spotifast).
       - Else if `wasPending`, return `CancelPendingResume` (browser resumed within the 350ms anti-seek window; keep `PausedByUs` intact).
       - Else return `None` (Spotifast was already paused manually; `PausedByUs` stays `false`).
     - **While browser keeps playing (`wasBrowserPlaying && anyBrowserPlaying`):**
       - If `PausedByUs && !awaitingPauseConfirmation && spotifastPlaying` (user manually resumed Spotifast over a video), clear `PausedByUs = false` and do **not** pause it again.
       - Return `None`.
     - **Falling edge (`wasBrowserPlaying && !anyBrowserPlaying`):**
       - If `PausedByUs`:
         - If `!spotifastExists || (!awaitingPauseConfirmation && spotifastPlaying)`, clear `PausedByUs = false` and `awaitingPauseConfirmation = false`, return `None`.
         - Set `ResumePending = true` and return `ScheduleResume` (arms a 350ms anti-seek-flicker timer).
       - Else return `None`.
     - **While browser remains stopped (`!wasBrowserPlaying && !anyBrowserPlaying`):**
       - If `ResumePending && (!spotifastExists || (!awaitingPauseConfirmation && spotifastPlaying))` (user manually resumed or closed Spotifast during the 350ms window), clear `ResumePending = false`, `PausedByUs = false`, `awaitingPauseConfirmation = false`, and return `CancelPendingResume`.
       - Return `None`.
     - **Resume timer elapsed (`350ms`):**
       - Clear `ResumePending = false` and `awaitingPauseConfirmation = false`.
       - If `Enabled && PausedByUs && !anyBrowserPlaying && spotifastExists && !spotifastPlaying`, clear `PausedByUs = false` and return `ResumeSpotifast`.
       - Otherwise clear `PausedByUs = false` and return `None`.
     - **Why 350ms anti-seek debounce on resume (0ms on pause):** Pausing is always instantaneous (`0ms`). Waiting `350ms` before resuming feels immediate to the ear while preventing brief YouTube timeline seeks/buffering flickers (`Playing → Paused → Playing`) from letting Spotifast burst audio for a fraction of a second. Configured as a single constant `ResumeDebounceMs = 350`.

3. **`SmtcBridge` (`src/Program.cs`)**
   - Requests `GlobalSystemMediaTransportControlsSessionManager.RequestAsync()`.
   - Subscribes to `SessionsChanged` and attaches/detaches `PlaybackInfoChanged` handlers on all active sessions (re-syncing cleanly when sessions appear or close).
   - Marshals every WinRT callback onto the WinForms UI thread (`Control.BeginInvoke`) so all state transitions, timers, and tray updates execute sequentially on a single thread with zero race conditions.
   - Executes `TryPauseAsync()` / `TryPlayAsync()` on the Spotifast session:
     - If `TryPauseAsync()` returns `false` while Spotifast is confirmed `Playing`, attempts `TryTogglePlayPauseAsync()` once as a safe fallback.
     - Same fallback for `TryPlayAsync()` while Spotifast is confirmed `Paused`.
   - Wraps all WinRT calls in `try/catch` (`COMException`, `ObjectDisposedException`, `InvalidOperationException`) so closing or restarting Spotifast or a browser never crashes `pauto`.
   - Exposes `GetSessionSnapshot()` returning `(AppId, PlaybackStatus, SessionKind)` for diagnostic inspection from the tray menu.

4. **`TrayApplicationContext` (`src/Program.cs`)**
   - Runs a `NotifyIcon` in the Windows notification area with programmatically drawn minimal 16x16 icons (green = monitoring, amber = Spotifast paused by `pauto`, gray = disabled) — zero external `.ico` asset files required.
   - Context menu items:
     - Status label (disabled menu item showing current state, e.g. `Status: Monitorando` / `Status: Spotifast pausado pelo navegador` / `Status: Desativado`)
     - `Ativo` (checked toggle)
     - `Iniciar com o Windows` (checked toggle managing `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\pauto` pointing to the current `pauto.exe` path)
     - `Ver sessões de mídia` (displays a simple `MessageBox` listing every currently visible SMTC session ID, classification, and playback status)
     - `Sair` (disposes tray icon, detaches SMTC listeners, exits cleanly)

## 4. Error Handling

- **SMTC unavailable at startup:** Shows a clear non-fatal tray balloon / message explaining that Windows Media Controls could not be initialized and exits cleanly.
- **Spotifast not running at startup:** Normal state — `pauto` stays idle and automatically hooks Spotifast as soon as it starts playing.
- **Session disappears mid-command:** Caught and ignored; state resets cleanly on the next `SessionsChanged`.
- **Multiple instances:** Second launch exits silently without interfering with the running instance.

## 5. Verification & Testing Strategy

1. **Automated unit tests (`tests/AutoPauseLogicTests.cs`):**
   - Executed via `build.ps1` using `csc.exe` (zero test framework dependencies).
   - Verifies:
     - Classifier recognizes Spotifast, common browsers (including Firefox AUMID hash), and ignores non-browser apps (e.g., VLC, Discord, Telegram).
     - Browser play pauses Spotifast → browser pause schedules & triggers resume after debounce.
     - Manually paused Spotifast is never auto-resumed when browser stops.
     - Manually resuming Spotifast while browser is playing clears ownership and never fights the user.
     - Multiple browser sessions: stays paused until all browser sessions stop playing.
     - Brief browser pause/resume within 350ms cancels pending resume (anti-seek flicker).
     - Spotifast absent/closed during browser pause does not crash and clears `PausedByUs`.
     - Disabling monitoring clears state and ignores subsequent events; re-enabling resumes clean behavior.
2. **Live verification on Windows:**
   - Launch `dist/pauto.exe` on the user's desktop session with Spotifast and their browser open.
   - Open `Ver sessões de mídia` to confirm the exact `SourceAppUserModelId` reported by Spotifast and the browser.
   - Verify end-to-end: play music in Spotifast → play YouTube video → Spotifast pauses instantly → pause video → Spotifast resumes automatically → manually pause Spotifast → play & pause video → Spotifast stays paused.
