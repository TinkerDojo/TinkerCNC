# Changelog

All notable changes to TinkerCNC are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project uses
[Semantic Versioning](https://semver.org/).

## [Unreleased]

### Changed
- The 3D views (toolpath and machine) are navigated like Autodesk Fusion: middle-drag pans, Shift + middle-drag
  orbits about the point under the cursor (constrained to the vertical axis, never past the poles), the wheel zooms
  towards the cursor; left-drag orbits as well, touch gestures stay.
- Job handling on the machine page is one **Job** card: it offers a program (saved, on the machine, or from this
  computer with drag and drop), sets it up with the options on the left and a step-by-step summary of what
  starting will do, the estimated time and the Start button on the right, and while the job runs shows the time
  left, progress, tool and next tool change with larger Pause, Resume and Stop controls.
- Saved programs became **setups**: a named set of start options with any number of G-code files (one setup, a
  file per piece). Existing saved programs are migrated to one setup with one file each. Setups are picked and
  managed in the program dialog and on the new **Setups** page; saving a program offers an existing setup (add the
  file, optionally the options) or a new one. The data folder keeps `programs/` for the files; the settings are
  `SetupFilesDirectory` and `QueuedFilesDirectory`.
- The time-lapse job option records a time-lapse from the camera here (one frame every few seconds, into the
  recordings folder) instead of switching on the machine's own time-lapse (`M951`).

### Added
- A **job queue** per machine: files run one after the other with a setup's options and repeat counts, from the
  setup's files or dropped for these runs only. Every queued job starts paused (a buffered `M600`) until the
  machine's button is held or Resume is pressed, so the piece can be swapped. A failed job stops the queue with a
  retry from the interrupted tool operation. The queue is stored and survives a restart.
- **Start from** a tool operation: a program can begin at any of its tool changes with a trimmed copy that restates
  the modal state; after a job ended early, the Job card offers to run it again from the operation it was in, also
  for jobs started on the machine itself.
- First public release: machine discovery and manual machines, dashboard with positions, jog, go-to, console,
  air and bed cleaning, machine views (top, side, 3D), job start with work origin, probing, leveling, scan margin
  and job modes, saved programs, job tracking with tool timeline and measured operation times, notifications,
  live camera with recordings and snapshots, time-lapse playback, Windows and macOS installers with updates,
  a CLI and a full machine simulator.
- NuGet packages `TinkerCNC.Core` (library) and `TinkerCNC.Cli` (dotnet tool), and a container image for running
  the dashboard as a server on Linux (`linux/amd64`, `linux/arm64`).
