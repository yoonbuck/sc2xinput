# sc2xinput

[![Build](https://github.com/yoonbuck/sc2xinput/actions/workflows/build.yml/badge.svg)](https://github.com/yoonbuck/sc2xinput/actions/workflows/build.yml)

Exposes Steam Controllers (1st gen; wired, or through the wireless dongle) as virtual Xbox 360
controllers. Anything that understands XInput, Windows.Gaming.Input (UWP / Xbox app games),
GameInput, or the browser Gamepad API will see a normal Xbox controller. Steam doesn't have to be running.

## Requirements

- [ViGEmBus](https://github.com/nefarius/ViGEmBus) driver: `winget install ViGEm.ViGEmBus`

## Download

Grab a zip from [Releases](https://github.com/yoonbuck/sc2xinput/releases), extract it anywhere, and run `sc2xinput.exe`.

- `sc2xinput-<version>-win-x64.zip`: standalone, no .NET install needed (~45 MB)
- `sc2xinput-<version>-win-x64-framework-dependent.zip`: tiny, but needs the
  [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)
- No native ARM64 build: the ViGEm client library only ships x86/x64 native code.

Builds of every commit to `main` are attached to the [Build workflow](https://github.com/yoonbuck/sc2xinput/actions/workflows/build.yml) runs.
To cut a release, push a tag like `v1.0.0`.

## Build / run

Requires the .NET 10 SDK. Open `sc2xinput.sln` in Visual Studio, or:

```
dotnet build -c Release
bin\Release\net10.0-windows\sc2xinput.exe
```

The app runs in the notification area. Its icon is bright while a controller is bridged, dimmed
while waiting for one, and crossed out while paused. Click it for the menu:

- **Status**: connected controllers and their XInput player numbers
- **Enabled**: uncheck to pause, which hands the controller back to its built-in mouse/keyboard mode
- **Start with Windows**: adds or removes a per-user Run entry for the exe at its current path
- **Edit config** / **Reload config**: open `sc2xinput.json`, then apply your changes without restarting
- **Open log**: `sc2xinput.log` next to the exe

Each connected Steam Controller gets its own virtual pad, and controllers can be plugged in or
turned on while the app runs. The app turns off the controller's built-in keyboard/mouse
emulation ("lizard mode") while it's running and turns it back on when you pause or exit.

Quit Steam while using this: Steam drives the controller too, and its desktop layout will
fight with this app.

### Command-line modes

```
sc2xinput --console    run in a console window instead of the tray (Ctrl+C to quit)
sc2xinput --dump       print decoded raw reports, for debugging
sc2xinput --restore    turn mouse/keyboard mode back on (if the app was killed without cleaning up)
```

The exe is a GUI app, so cmd/PowerShell don't wait for it: its output appears after the next
prompt. Ctrl+C in that terminal still stops it.

## Default mapping

| Steam Controller      | Xbox 360                         |
|-----------------------|----------------------------------|
| A / B / X / Y         | A / B / X / Y                    |
| Bumpers / triggers    | LB / RB, analog LT / RT          |
| ◀ / ▶ / Steam        | Back / Start / Guide             |
| Stick (+ click)       | Left stick (L3)                  |
| Left pad (click)      | D-pad, 8-way                     |
| Right pad (+ click)   | Right stick, camera-style: swipe speed sets deflection (R3) |
| Left / right grip     | L3 / R3                          |
| Rumble                | Haptic pulses (large motor → left pad, small → right) |

## Steam button chords

The Steam button works as a modifier, like in Steam: hold it and press another button.

| Chord                     | Action                                                        | Confirmation           |
|---------------------------|---------------------------------------------------------------|------------------------|
| Steam + Y                 | Toggle capture: unplug the virtual pad and give the controller back its mouse/keyboard mode, or take it back | 2 ticks off, 1 tick on (both pads) |
| Steam + right pad click   | Switch the right pad between Camera and Stick mode (and `None`, if that's the configured mode). The pad is ignored until you lift your thumb, so the click doesn't jerk the camera | 1 tick camera, 2 ticks stick, 3 ticks none (right pad) |

Steam on its own still works as Guide: a quick tap sends a short Guide press when you let go, and
holding it for 0.4 s or longer holds Guide until you release (for Xbox long-press shortcuts). Once
Guide is being held, chords are off for the rest of that press, so press the second button within 0.4 s.

Each controller is toggled separately. Capture stays off while a wireless controller sleeps and across
Reload config; unchecking and re-checking Enabled in the tray captures every controller again.

The defaults use buttons that send nothing in the controller's mouse/keyboard mode, so turning
capture back on doesn't also type a key. In that mode, A/B are Enter/Escape, LB/RB are Space/F9, ◀/▶ are
Tab/Escape, the grips are F7/F9, the stick click is F4, the left pad clicks are arrow keys, and LT/RT are right/left click.

## Config

`sc2xinput.json` next to the exe (written with defaults on first run):

- `LeftGrip`, `RightGrip`, `SteamButton`, `RightPadClick`: any of `A B X Y LeftShoulder RightShoulder
  Back Start Guide LeftThumb RightThumb DPadUp DPadDown DPadLeft DPadRight None`
  (combine with commas, e.g. `"A, B"`).
- `LeftPadRequiresClick`: `false` makes the D-pad respond to touch instead of click.
- `LeftPadDeadzone`, `StickDeadzone`: fraction of full range (0–1).
- `RightPadMode`:
  - `Camera` (default): like Steam's Joystick Mouse. The stick deflects in proportion to how fast your
    finger moves, so a swipe turns the camera by an amount proportional to its length, and a still
    finger stops it. Config files written by older versions still say `"Stick"`; change it to switch.
    - `CameraSensitivity`: deflection per pad-radius-per-second of finger speed (default 0.27, so
      full deflection at about 4 radii/s). Pair it with the game's own look sensitivity.
    - `CameraAntiDeadzone`: deflection that any movement starts at, to get past the game's own stick
      deadzone. Lower it if slow movements are too fast, raise it if they do nothing.
    - `CameraJitterThreshold`: finger speed (pad radii/s) below which movement is ignored, fading in up
      to twice that. Raise it if the camera twitches while your thumb is resting.
    - `CameraSmoothingMs`: averaging window for finger speed. Higher is smoother but laggier.
    - `CameraTrackball`, `CameraTrackballFriction`: keep turning after a flick and coast to a stop.
  - `Stick`: finger position on the pad is the stick position. Tuned with `RightPadDeadzone` and
    `RightPadSensitivity` (above 1 reaches full deflection before your thumb hits the pad's edge).
  - `None`.
- `Rumble`, `RumbleStrength`.
- `SteamChords`: map of Steam Controller button → action, e.g. `{ "Y": "ToggleCapture", "RPadClick": "CycleRightPadMode" }`.
  Buttons: `A B X Y LB RB Back Start LGrip RGrip StickClick LPadClick RPadClick` (`Back`/`Start` are the ◀/▶ buttons). Actions: `ToggleCapture`,
  `CycleRightPadMode`, `None`. Set it to `{}` to turn chords off, so Steam presses Guide immediately.

## Limitations

- Bluetooth LE mode (controllers flashed with the BLE firmware) isn't supported; use the dongle or a cable.
- Gyro isn't mapped.
