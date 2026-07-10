# Sonar Mini Mixer Implementation Plan

> **For Hermes:** Implement autonomously with strict red-green-refactor cycles and verify the published executable.

**Goal:** Build a polished native Windows tray popup that safely controls SteelSeries Sonar's existing six-channel classic mixer.

**Architecture:** A testable .NET core owns endpoint discovery, parsing, transport, settings, and commands. A WPF executable owns tray/popup behavior and binds to an async view model. The same executable exposes read-only CLI diagnostics for headless verification.

**Tech Stack:** .NET 10, WPF, Windows Forms NotifyIcon, System.Text.Json, HttpClient, HKCU registry, dependency-free executable tests.

---

### Task 1: Scaffold and core contracts
Create the solution, Core/App/Tests projects, immutable mixer models, interfaces, and a test runner. Write tests first for channel mapping and validation; run them red, implement, run green.

### Task 2: Secure endpoint discovery
Test and implement `coreProps.json` parsing, loopback-only URI validation, HTTPS GG discovery, dynamic Sonar address resolution, and one rediscovery retry. Reject credentials, fragments, non-loopback DNS/IP addresses, and unsupported schemes.

### Task 3: Sonar API client
Test and implement state reads and classic-mode writes for volume, mute, and ChatMix. Enforce numeric bounds, safe channel IDs, classic-mode guard, cancellation, and HTTP success checks.

### Task 4: Settings and startup
Test corrupt/missing settings recovery and atomic persistence. Implement opt-in HKCU Run registration and a settings window with launch-at-login and close-to-tray choices.

### Task 5: Tray popup and view model
Implement single-instance startup, tray icon/menu, compact popup positioning on the active work area, pin/unpin state, deactivation dismissal, polling, write debounce, keyboard controls, mute toggles, connection errors, and graceful shutdown.

### Task 6: Visual system
Implement the compact dark horizontal six-fader layout, custom vertical sliders, accessible focus/hover states, high-DPI behavior, reduced-motion-safe feedback, and clear offline/unsupported-mode states.

### Task 7: CLI diagnostics and packaging
Add `config`, `status`, `selftest`, `show`, and `exit` verbs. Keep verification verbs read-only. Publish self-contained win-x64 single-file output and create a Start Menu shortcut without enabling startup automatically.

### Task 8: Verification and review
Run all tests, release build, live read-only status/selftest against Sonar 1.97, published executable checks, one-shot UI Automation layout/interaction-neutral QA, log scan, idle CPU/memory probe, and independent security/code/UX review. Fix all concrete findings and rerun gates.
