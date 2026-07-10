# Sonar Mini Mixer Design

## Goal
A tiny native Windows tray controller for the existing SteelSeries Sonar mixer, providing six channel faders, mute controls, and ChatMix without opening SteelSeries GG.

## Approved interaction
The application runs as a single-instance notification-area utility. Clicking its tray icon opens a compact 690 x 330 borderless popup above the taskbar. The popup dismisses on deactivation unless pinned. Pinning makes the window movable/resizable and keeps it above normal windows. The app never processes audio or changes Windows routing.

## UI
- Header: product name, live connection status, pin, settings, and close buttons.
- Mixer: Master, Game, Chat, Media, Aux, and Mic vertical faders with distinct accents, percentage labels, and mute buttons.
- Footer: horizontal Game/Chat balance slider with center reset.
- Dark graphite/Dracula-inspired surface with restrained color, keyboard focus indicators, tooltips, and automation names.
- Disconnected and unsupported-mode states remain visible and disable writes without destroying the last known state.

## Architecture
- `SonarMiniMixer.Core`: models, endpoint validation/discovery, HTTP API client, settings, and startup registration abstractions.
- `SonarMiniMixer.App`: WPF tray lifecycle, popup window, view model, controls, settings dialog, and CLI diagnostics.
- `SonarMiniMixer.Tests`: dependency-free executable test harness for deterministic core tests.

The API client reads `%ProgramData%\SteelSeries\SteelSeries Engine 3\coreProps.json`, validates that both discovered addresses are HTTP(S) loopback endpoints, accepts GG's local certificate only for loopback requests, then accesses the Sonar service. A failed request invalidates discovery and retries once. Writes are serialized and UI slider writes are debounced.

## Persistence and startup
Settings live at `%LocalAppData%\SonarMiniMixer\settings.json`. Startup uses the current-user `Run` registry entry and is opt-in from Settings. No admin rights are required.

## Verification
- Unit tests cover parsing, loopback rejection, URI construction, value bounds, settings recovery, and state mapping.
- Fake HTTP integration tests cover discovery, reads, writes, rediscovery, and classic-mode write protection.
- CLI `status`, `selftest`, and `config` commands provide headless live verification; selftest is read-only.
- Release publish is exercised from its final folder. Native UI QA launches one minimized/non-activating pass, inspects UI Automation, memory and idle CPU, and exits through CLI IPC without changing volume.
