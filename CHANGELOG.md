# Changelog

All notable changes to TinkerCNC are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project uses
[Semantic Versioning](https://semver.org/).

## [Unreleased]

### Added
- First public release: machine discovery and manual machines, dashboard with positions, jog, go-to, console,
  air and bed cleaning, machine views (top, side, 3D), job start with work origin, probing, leveling, scan margin
  and job modes, saved programs, job tracking with tool timeline and measured operation times, notifications,
  live camera with recordings and snapshots, time-lapse playback, Windows and macOS installers with updates,
  a CLI and a full machine simulator.
- NuGet packages `TinkerCNC.Core` (library) and `TinkerCNC.Cli` (dotnet tool), and a container image for running
  the dashboard as a server on Linux (`linux/amd64`, `linux/arm64`).
