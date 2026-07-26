# Sonar Mini Mixer Design

## Goal
A tiny native Windows tray controller for the existing SteelSeries Sonar mixer, providing six channel faders, mute controls, and ChatMix without opening SteelSeries GG.

## Approved interaction
The application runs as a single-instance notification-area utility. Clicking its tray icon opens a compact borderless popup above the taskbar (default 864 x 424, resizable between 640 x 372 and 1180 x 650). The popup dismisses on deactivation unless pinned. Pinning makes the window movable/resizable and keeps it above normal windows. The app never processes audio or installs devices.

## UI
- Header: product mark, name, live connection status, pin, settings, and close buttons. The header uses the tile-free `AppMark`; the windowed/tray/Start Menu identity uses the tiled `AppIcon`.
- Mixer: Master, Game, Chat, Media, Aux, and Mic vertical faders with distinct accents, percentage labels, and mute buttons.
- Per channel: an EQ preset selector above the fader and a physical device selector (OUT for playback, IN for Mic) below the mute button.
- Master column: an "all playback outputs" selector that fans Game, Chat, Media, and Aux to one device without touching Mic.
- Footer: horizontal Game/Chat balance slider with center reset.
- Near-black OLED surface with a subtle plum backdrop, restrained per-channel color, keyboard focus indicators, tooltips, and automation names on every control.
- Layout metrics scale with window size so the mixer stays legible from the minimum to the maximum bounds.
- Disconnected and unsupported-mode states remain visible and disable writes without destroying the last known state.

## Architecture
- `SonarMiniMixer.Core`: models, endpoint validation/discovery, HTTP API client, settings, and startup registration abstractions.
- `SonarMiniMixer.App`: WPF tray lifecycle, popup window, view model, controls, settings dialog, and CLI diagnostics.
- `SonarMiniMixer.Tests`: dependency-free executable test harness for deterministic core tests.
- `SonarMiniMixer.App.Tests`: dependency-free WPF/UI-automation test harness for view-model and control behaviour.

The API client reads `%ProgramData%\SteelSeries\SteelSeries Engine 3\coreProps.json`, validates that both discovered addresses are HTTP(S) loopback endpoints, accepts GG's local certificate only for loopback requests, then accesses the Sonar service. A failed request invalidates discovery and retries once. Writes are serialized and UI slider writes are debounced. Preset and routing catalogs refresh on a slower cadence than mixer state so option loading never blocks live faders, and an options failure degrades to a status message instead of disabling the mixer.

## Persistence and startup
Settings live at `%LocalAppData%\SonarMiniMixer\settings.json`. Startup uses the current-user `Run` registry entry and is opt-in from Settings. No admin rights are required.

## Verification
- Unit tests cover parsing, loopback rejection, URI construction, value bounds, settings recovery, and state mapping.
- Fake HTTP integration tests cover discovery, reads, writes, rediscovery, and classic-mode write protection.
- UI tests cover fader hit targets, mute iconography and toggle semantics, responsive metrics, theme constraints, selector chrome, per-channel option loading, Master fan-out behaviour, and pending-edit preservation across polls.
- CLI `status`, `selftest`, and `config` commands provide headless live verification; selftest is read-only.
- Release publish is exercised from its final folder. Native UI QA launches one minimized/non-activating pass, inspects UI Automation, memory and idle CPU, and exits through CLI IPC without changing volume.
