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
- `SessionCore.cs` (+ `SessionEvents.cs`, `InputTracker.cs`, `OutputStage.cs`): all per-controller logic
  (capture, lizard mode, chords, camera, rumble, retries) as a pure state machine, `Step(event, now) →
  effects`, with an explicit lifecycle (Asleep / Gamepad / Releasing / Mouse / Closed).
- `ControllerSession.cs`: the shell around it: one loop thread per controller that feeds the core events,
  carries out its effects (ViGEm, HID commands) and waits for the next event or deadline (`Native/HighResWait.cs`).
- Pure logic, no I/O: the core files above, `SteamChords.cs` (Steam-button chords and Guide timing),
  `CameraPad.cs` (right-pad camera mode), `Mapper.cs` (Steam Controller → Xbox mapping),
  `SteamController/ScProtocol.cs` (report parsing). Keep these free of threads, blocking, clocks and device
  calls (a test enforces it); time is passed in.
- `SteamController/ScDevice.cs`: HID I/O. `Native/`: Win32 interop.
- `tests/Sc2XInput.Tests`: xunit.v3 tests. `tools/Sc2XInput.DevTools`: hardware diagnostics.

## Architecture and threading

Behavior lives in `SessionCore`; `ControllerSession` only translates. Before this design, a session was
touched by five threads under one lock, and most bugs were state not reset at some transition, races, or
timer callbacks after dispose. Keep it that way:

- **One owner.** Only the session loop thread touches session state. Other threads (the HID reader, ViGEm's
  rumble callback) post events; nothing else locks or mutates. Haptic pulses run on a separate worker only
  because HID writes are slow; it holds no state.
- **New behavior goes in the core**, as events, effects and deadlines, never as timers or tasks in the shell.
  Add a scenario test for it; the randomized test checks invariants after every step.
- **New derived state** belongs in `InputTracker` (lives as long as the link; replaced on link loss) or
  `OutputStage` (exists only while bridging as a gamepad; rebuilt on plug and mode switch). Transitions
  construct fresh state rather than resetting fields.
- **Effect contract:** the shell runs every effect in a batch, skipping a `Submit` whose `PlugPad` failed in
  that batch, then steps an `EffectFailed` for each failure, in order, before any other event. Success is
  inferred from the next non-failure event. `Close` skips the queue.
- The shell catches exceptions from effects and from the core; an unhandled exception on any thread ends
  the process.

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
- `haptics <segment>...`: plays pulse trains straight to the trackpads (app not running), for tuning
  haptics by feel without rebuilding.
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
- Haptics: the firmware numbers the pads right = 0, left = 1, and ignores the pulse command's dB gain and
  "both pads" value. Strength comes only from each pulse's on-time, which plateaus after ~1.5-2 ms (and
  weakens near 50% duty). Each actuator resonates unevenly: 80 Hz and 160-200 Hz are strong, 120 Hz barely
  registers. A pulse train builds into a rumble; single pulses sent one command at a time (e.g. random timing
  for "noise") feel weak and tappy. See the rumble constants in `SessionCore`.
