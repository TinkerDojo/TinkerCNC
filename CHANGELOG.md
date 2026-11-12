# Changelog

All notable changes to TinkerCNC are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project uses
[Semantic Versioning](https://semver.org/).

## [Unreleased]

## [0.3.1] - 2026-11-12

### Fixed
- A job **started from a tool operation** or **resumed after a stop** no longer runs its rapid moves at the cutting
  feed. The copy restated the feed on a rapid move (`G0 F300`), which the machine takes as the speed of every later
  rapid; the feed now goes on a `G1`, and the rapids keep the machine's own speed.

## [0.3.0] - 2026-11-10

### Added
- **Resume after a stop**: where a running job is gets stored per machine (every 30 s, and at once when it ends
  early). After an emergency stop, an alarm or the machine switched off, also after reopening the app, the Job card
  offers to resume at that line: the work origin is restored, the tool asked for and measured, the head brought back
  to that point, and the program goes on from there.
- The dashboard speaks **English, German, French and Italian**. It follows the browser's (or, in the app's window,
  the system's) language; **Settings → Language** picks another one for this browser.
- **System notifications** (Settings → Notifications): tool changes, alarms and queue news also appear as the
  system's own notifications, even while the dashboard is in the background; a click opens the machine.

### Changed
- The **toolpath** view shows the program in the machine: the machine around it and the toolpath at the work origin,
  where it really cuts. Two tabs: **Live** (what has been cut so far) and **Preview** (the whole toolpath once the job
  has run); a Machine button frames the whole machine.
- **Start** on the Job card (now **Current job**) starts the queue with that job as its first entry: its run count can
  be raised while it runs, and further runs and queued entries follow, each waiting for the go.
- Running a file from this computer, a setup or the queue again **no longer uploads it again** when the machine
  already has it: the MD5 the machine announces for that file is compared first, without transferring anything.
- Camera recordings and job time-lapses are **MP4 files** (H.264) that every player and browser opens, instead of
  raw JPEG streams. The encoder is part of TinkerCNC (no ffmpeg or other native codec is needed); recordings cut
  short by closing the app are completed at the next start. Older `.mjpeg` recordings stay where they are.
- A camera recording keeps **every fifth frame** (in real time), a fifth of the data of recording every frame. While
  it runs, its file's size in Windows Explorer grows with it.
- A job's time-lapse records **one frame every second** (it used to stretch the interval up to a minute for long
  jobs) and plays 30 times faster than the job.
- **Start queue** works while a job runs: the running job counts as the queue's first, and the queue goes on once it
  has finished (it used to require an Idle machine).

## [0.2.0] - 2026-11-07

### Added
- A **job queue** per machine: files run one after the other with a setup's options and repeat counts, from the
  setup's files or dropped for these runs only. Every queued job starts paused (a buffered `M600`) until the
  machine's button is held or Resume is pressed, so the piece can be swapped. A failed job stops the queue with a
  retry from the interrupted tool operation. The queue is stored and survives a restart.
- **Start from** a tool operation: a program can begin at any of its tool changes with a trimmed copy that restates
  the modal state; after a job ended early, the Job card offers to run it again from the operation it was in, also
  for jobs started on the machine itself.

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

## [0.1.0] - 2026-11-06

### Added
- First public release: machine discovery and manual machines, dashboard with positions, jog, go-to, console,
  air and bed cleaning, machine views (top, side, 3D), job start with work origin, probing, leveling, scan margin
  and job modes, saved programs, job tracking with tool timeline and measured operation times, notifications,
  live camera with recordings and snapshots, time-lapse playback, Windows and macOS installers with updates,
  a CLI and a full machine simulator.
- NuGet packages `TinkerCNC.Core` (library) and `TinkerCNC.Cli` (dotnet tool), and a container image for running
  the dashboard as a server on Linux (`linux/amd64`, `linux/arm64`).
