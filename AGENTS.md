# AGENTS.md

Guidance for AI agents (and humans) working on sc2xinput. Keep it short and current; details belong in
code comments, the README, or PR descriptions.

## What this is

A Windows tray app that turns a 1st-gen Steam Controller (wired, or via its wireless dongle) into a
virtual Xbox 360 controller through the ViGEmBus driver, without Steam. C# / .NET 10, WinForms for the
tray. Behavior is documented for users in `README.md`.

## Layout

- `Program.cs`: entry point and CLI modes (`--console`, `--dump`, `--restore`).
- `TrayApp.cs`: tray icon and menu. `Bridge.cs`: finds controllers and owns one session per HID interface.
- `ControllerSession.cs`: bridges one controller to one virtual pad (capture, lizard mode, rumble, chords).
- Pure logic, no I/O: `SteamChords.cs` (Steam-button chords and Guide timing), `CameraPad.cs` (right-pad
  camera mode), `Mapper.cs` (Steam Controller → Xbox mapping), `SteamController/ScProtocol.cs` (report
  parsing). Keep these free of thread creation, blocking, clocks and device calls; time is passed in.
- `SteamController/ScDevice.cs`: HID I/O. `Native/`: Win32 interop.
- `tests/Sc2XInput.Tests`: xunit.v3 tests. `tools/Sc2XInput.DevTools`: hardware diagnostics.

## Architecture and threading

Today `ControllerSession` is touched by several threads (HID reader, chord timer, camera ticker, rumble
timer, feedback tasks); most of it under one session lock, but the rumble timer, feedback tasks and
player-index polling run outside it. Most past bugs came from that: state not reset at a transition,
races, timer callbacks after dispose. A refactor is underway to a single-threaded loop per controller
with a pure `SessionCore.Step(event, now) → effects` and an explicit lifecycle state; update this section
when it lands. Until then:

- Every piece of state derived from input has to be cleared at link loss (`ResetInput`) or when the
  output changes (`ResetOutput`). Add new state to those, not to individual call sites.
- Anything that runs on a timer or background thread must catch its own exceptions; an unhandled one
  ends the process.
- Never block or join while holding the session lock; ticks take it.

## Build and test

- `dotnet build sc2xinput.sln` (the SDK may not be on PATH: `"/c/Program Files/dotnet/dotnet"`).
- `dotnet test --project tests/Sc2XInput.Tests` (`global.json` opts into Microsoft.Testing.Platform, which
  xunit.v3 needs on the .NET 10 SDK).
- A running `sc2xinput.exe` locks `bin/Release`. Build Debug, or exit it from the tray first. Killing it
  (taskkill) skips restoring mouse mode; run `sc2xinput --restore` or power-cycle the controller after.
- Use synthetic time in `Stopwatch` ticks in tests (`Time.Ms`). Every bug fix gets a test.
- Timing/feel code is tested against `tests/Sc2XInput.Tests/Fixtures/right-pad-recording.csv`, real
  pad data at the dongle's 8-12 ms report cadence. Record more with `sc2xinput-devtools record-pad`.

## Verifying on hardware

Unit tests don't cover feel, the firmware, or the driver. Before merging behavior changes, run the app
and check with the user, using `tools/Sc2XInput.DevTools`
(`dotnet run --project tools/Sc2XInput.DevTools -- <command>`):

- `xinput`: what games see, including Guide (uses `XInputGetStateEx`; plain `XInputGetState` hides Guide).
- `rumble <large> <small> <ms>`: drives rumble through XInput.
- `inputlog`: controller button edges plus keyboard/mouse input coming from the controller itself, on one
  timeline. Shows lizard-mode keys and any keypresses leaking during capture toggles.
- `record-pad`: right-pad samples to CSV for new fixtures.

Typical checklist: buttons/sticks/triggers, camera feel, Steam tap and hold (Guide), Steam+Y capture
off/on (no keys leaked), Steam+right-pad-click, rumble, controller sleep/wake, tray Reload config, Exit
restores mouse mode.

## Workflow

- One branch and PR per change; don't stack PRs (merging a base branch with `--delete-branch` closes PRs
  that target it). Merge with merge commits. The user merges unless they say otherwise.
- For stateful changes, write the state/transition table before code, and list which state each
  transition must reset.
- Self-review before opening a PR (fresh review agents per branch, told what was already fixed), then
  the Copilot review loop: verify each finding against the code, fix, reply on the thread with the
  commit, resolve it. Findings in a review's summary (not inline) get a PR comment.
- Merge bar: fix high/medium findings; file unlikely low-severity edge cases as issues instead of
  iterating indefinitely.
- `gh` may not be on PATH: `"/c/Program Files/GitHub CLI/gh.exe"`.
- Commit messages explain why; end with the `Co-Authored-By` line for AI-authored commits.

## Conventions

- Match the surrounding style: small methods, comments only where the why isn't obvious, XML doc
  summaries on types and non-obvious members.
- Config options live in `Config.cs` with a doc comment and are documented in the README. Defaults that
  affect feel (camera sensitivity etc.) were tuned on hardware with the user; don't change them casually.
- Don't change user-visible behavior in refactors; call out any unavoidable change in the PR.

## Gotchas learned so far

- **Lizard mode** is the controller's built-in keyboard/mouse mode. It must be off while bridging and
  restored on exit/pause. Turning it on while a button is held makes the firmware type that button's
  key. Key map: A/B Enter/Escape, X/Y nothing, LB/RB Space/F9, ◀/▶ Tab/Escape, grips F7/F9, stick click F4,
  left pad clicks arrow keys, right pad click nothing, LT/RT right/left click, Steam nothing. Chord
  buttons must be ones that send nothing (X, Y, right pad click).
- The wireless dongle exposes all four slots as HID interfaces even when empty; connection comes from
  connection-status reports. Wired controllers have no such events.
- Reports arrive every ~8-12 ms over the dongle (in 4 ms slots), and only when input changes.
- ViGEm: a new virtual pad reports junk stick values until it receives a report that differs from
  all-zero; the code nudges it after `Connect`. `Timer.Change` after `Dispose` returns false on .NET 10
  but is documented to throw.
- Windows 11 ignores `timeBeginPeriod` for windowless processes; use high-resolution waitable timers.
- The ◀/▶ buttons are Back/Start in code and config.
