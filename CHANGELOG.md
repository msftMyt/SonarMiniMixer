# Changelog

All notable changes to Sonar Mini Mixer are documented here. This project follows [Semantic Versioning](https://semver.org/).

## [Unreleased]

### Changed

- **Push-first synchronization.** Sonar's local `/sock` WebSocket now drives volume, mute, ChatMix, routing, fallback-device, and reconnect updates; visible polling is only a safety net and hidden tray mode does not poll.
- **Lightweight hidden startup.** The tray and IPC server start without constructing the WPF mixer, Sonar clients, or audio endpoint inventory until the mixer is first opened.
- **Fast, restart-safe Core Audio writes.** The app caches only Sonar endpoint IDs and opens a fresh short-lived `MMDevice` handle per write. This avoids stale COM handles while reducing measured channel writes from roughly 206 ms to 1–2 ms.

### Fixed

- Current GG compatibility: ChatMix is discovered at `/v1/chatMix` with legacy fallback and re-discovery after GG changes endpoint shape.
- Sonar restart recovery now waits through GG's transient empty-address startup state, reconnects the event socket on the UI dispatcher, and immediately rebuilds routing/options.
- External GG volume/mute changes apply directly from `SONAR_EVENT_VOLUME_DATA`; selected EQ presets refresh without re-downloading all preset catalogs, including newly created custom presets.
- Duplicate launches no longer deadlock WPF startup or hang on an unbounded named-pipe write; a shutdown/start race can take over the released mutex safely.
- Exiting from the tray before ever opening the mixer no longer overwrites saved window size, pin, or position with defaults.
- Dead Sonar routes (`isRunning=false`) display an explicit red warning instead of appearing healthy.

## [1.1.1] - 2026-07-26

### Changed

- **New application icon.** Three channel faders with green/blue/amber level fills on a near-black tile, matching the mixer's OLED surface instead of the old purple monogram. A tile-free `AppMark` variant is used inside the app header so the mark does not sit in a box on the dark chrome.
- The **Mic channel's mute button now uses a microphone icon** instead of a speaker, so muting your voice reads differently from muting a playback channel. Muted state slashes the mic the same way it slashes a speaker.

## [1.1.0] - 2026-07-25

### Added

- **Per-channel EQ presets.** Game, Chat, Media, Aux, and Mic each expose their Sonar preset list above the fader, with the active preset preselected and favorites ordered first.
- **Per-channel physical routing.** Every playback channel has its own **OUT** device selector and the microphone has its own **IN** selector, matching Sonar's Classic routing model instead of one global output.
- **Master quick output.** The Master column can send Game, Chat, Media, and Aux to a single output device in one action. The microphone is never changed by this control.
- **WPF UI test suite** (`SonarMiniMixer.App.Tests`) covering fader hit targets, mute iconography and toggle semantics, responsive layout metrics, theme constraints, selector chrome, option loading, Master fan-out, and pending-edit preservation. It runs in CI next to the core suite.
- **Assembly version metadata** via `Directory.Build.props`, so released binaries report a real product version.

### Changed

- **Rebuilt UI on a near-black OLED surface.** Restrained per-channel accent color, a subtle plum backdrop, an open instrument layout, and no boxed values or busy tick marks.
- **Responsive layout.** Metrics interpolate between the minimum (640x372), reference (864x424), and maximum (1180x650) window sizes, so the mixer stays legible and uncluttered while resizing.
- **Wider fader hit targets** with a visible groove and accent level fill, plus mouse-wheel and arrow-key stepping on the focused fader.
- **ChatMix footer** now uses a **Reset** action (`Ctrl+0` still works).
- **Accessibility.** Every mixer control exposes an automation name; mute buttons expose toggle semantics and a checked state.

### Fixed

- **Live edits are no longer clobbered by the poll loop.** A pending volume or ChatMix edit survives a refresh and is written afterward.
- **Preset and routing failures degrade gracefully.** They now surface a status message instead of disabling the core mixer controls.
- **Preset and routing catalogs refresh on a slower cadence** than mixer state, so option loading never stalls live faders.
- **Read-only channels reject local drift**, and a failed volume write surfaces an actionable status instead of silently diverging from Sonar.
- **QA thresholds corrected.** Window-bounds and working-set checks matched an older, smaller build and reported false failures.

## [1.0.1] - 2026-07-09

### Fixed

- Channel volume and mute changes route through the corresponding Windows Core Audio endpoints so Sonar observes them and SteelSeries GG's mixer UI stays synchronized.
- Sonar REST stays read-only for mixer state; ChatMix uses its dedicated API only when Sonar reports it enabled.
- Added fail-closed mode and ChatMix capability handling, finite numeric validation, and transport-only retry behavior.

## [1.0.0] - 2026-07-09

### Added

- Initial release: tray mixer for Sonar's six Classic channels, volume and mute control, ChatMix with center reset, pinned mode, automatic reconnection, optional Windows startup, and a read-only diagnostics CLI.

[1.1.1]: https://github.com/msftMyt/SonarMiniMixer/releases/tag/v1.1.1
[1.1.0]: https://github.com/msftMyt/SonarMiniMixer/releases/tag/v1.1.0
[1.0.1]: https://github.com/msftMyt/SonarMiniMixer/releases/tag/v1.0.1
[1.0.0]: https://github.com/msftMyt/SonarMiniMixer/releases/tag/v1.0.0
