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
| Back / Start / Steam  | Back / Start / Guide             |
| Stick (+ click)       | Left stick (L3)                  |
| Left pad (click)      | D-pad, 8-way                     |
| Right pad (+ click)   | Right stick, camera-style: swipe speed sets deflection (R3) |
| Left / right grip     | L3 / R3                          |
| Rumble                | Haptic pulses (large motor → left pad, small → right) |

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
    finger stops it.
    - `CameraSensitivity`: deflection per pad-radius-per-second of finger speed (default 0.25, so
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

## Limitations

- Bluetooth LE mode (controllers flashed with the BLE firmware) isn't supported; use the dongle or a cable.
- Gyro isn't mapped.
