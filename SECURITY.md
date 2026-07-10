# Security Policy

## Supported versions

Only the latest release is supported.

## Reporting a vulnerability

Please use GitHub's private vulnerability reporting feature when available. Do not open a public issue containing credentials, `coreProps.json`, local service addresses, personal file paths, or other machine-specific data.

Include the affected version, reproduction steps, impact, and a minimal redacted log. You should receive an initial response within seven days.

## Security model

Sonar Mini Mixer is a local desktop controller. It validates that discovered SteelSeries services are loopback-only and restricts its named-pipe commands to the current Windows user. It does not provide a network server or collect telemetry.
