using Sc2XInput.SteamController;

namespace Sc2XInput;

// Vocabulary of SessionCore: what the shell feeds it (events), what it asks the shell to do (effects),
// and what it reports (status). Timestamps are Stopwatch ticks.

internal abstract record SessionEvent;

/// <summary>The controller is there: dongle connection event, or the shell starting a wired controller.</summary>
internal sealed record Connected : SessionEvent;

/// <summary>The wireless link dropped (controller asleep or out of range).</summary>
internal sealed record Disconnected : SessionEvent;

/// <summary>The HID interface itself is gone (unplugged, read error). Terminal.</summary>
internal sealed record DeviceLost : SessionEvent;

/// <summary>A 64-byte input report (report-ID byte stripped), copied off the reader thread, and when it arrived.</summary>
internal sealed record Input(byte[] Report, long Timestamp) : SessionEvent;

/// <summary>Time has reached <see cref="SessionCore.NextDeadline"/>.</summary>
internal sealed record Tick : SessionEvent;

/// <summary>Rumble levels a game set on the virtual pad of generation <paramref name="PadGeneration"/>.</summary>
internal sealed record Rumble(int PadGeneration, byte Large, byte Small) : SessionEvent;

/// <summary>Answer to <see cref="QueryPlayerIndex"/>; null if XInput hasn't assigned a slot yet.</summary>
internal sealed record PlayerIndexResult(int PadGeneration, int? Index) : SessionEvent;

/// <summary>
/// The shell couldn't carry out <paramref name="Effect"/>. The shell still runs the rest of that step's
/// effects (skipping a Submit when there is no pad), then steps each failure in order, before any queued event.
/// </summary>
internal sealed record EffectFailed(Effect Effect) : SessionEvent;

/// <summary>
/// Shut down: unplug and give the controller its mouse/keyboard mode back. Terminal. May be stepped ahead of
/// pending failures; the core then treats an unconfirmed mouse-mode restore as not done.
/// </summary>
internal sealed record Close : SessionEvent;

internal abstract record Effect;

/// <summary>
/// Create and connect a virtual Xbox 360 pad, then nudge it with a non-zero report and a zero report (XInput
/// reports junk until it sees one). If anything fails after connecting, the shell disconnects it again
/// before reporting the failure.
/// </summary>
internal sealed record PlugPad(int Generation) : Effect;

/// <summary>Disconnect the virtual pad. Never reported as failed.</summary>
internal sealed record UnplugPad : Effect;

internal sealed record Submit(XState State) : Effect;

/// <summary>Turn the controller's built-in keyboard/mouse ("lizard") mode on or off.</summary>
internal sealed record SetLizard(bool Enabled) : Effect;

/// <summary>Haptic pulse train on one trackpad. Fire-and-forget: never reported as failed.</summary>
internal sealed record Haptic(ScPad Pad, ushort OnUs, ushort OffUs, ushort Count) : Effect;

internal sealed record QueryPlayerIndex(int Generation) : Effect;

internal enum LogLevel { Info, Warn, Error }

/// <summary>Immutable snapshot for the tray and Bridge.</summary>
/// <param name="Present">A physical controller is connected.</param>
/// <param name="Captured">The user's choice: bridged as a gamepad (true) or handed back to mouse mode.</param>
internal sealed record SessionStatus(bool Present, bool Captured, bool IsClosed, int? Player, RightPadMode Mode);
