# Dashboard features

What every part of the web dashboard does, in detail. The [README](../README.md) has the short version; the wire
protocol behind it is in [`protocol.md`](protocol.md).

## Machines

The machines page merges two sources into one list:

- **live UDP discovery** on port 3333, with an online/busy indicator (a machine that stops announcing itself goes
  offline after 10 s);
- **machines added by address** (`ip` or `ip:port`), stored in `machines.json` in the data folder. "Query" sends the
  unicast `QUERY` probe to that address and fills in the name the machine answers with.

A machine first added by address that later announces itself by name is merged into one entry. Each machine has one
connection shared by every browser tab that opens it.

## Dashboard

- **Positions**: work and machine positions (X/Y/Z/A) plus every other status field the machine reports.
- **Controls**: Hold, Resume, Home (`$H`), Unlock (`$X`) and Reset (Ctrl-X).
- **Jog**: `G91 G0 …` followed by `G90`, in selectable steps.
- **Go to**: typed X/Y/Z (work or machine coordinates), or click a point in the top view. Values outside the
  machine's travel are refused. Moves are rapids in machine coordinates (`G53 G0`): up to the clearance height
  first, across, then down to the typed Z (without one, the tool stays at the clearance height).
- **Machine view**: a top view of the travel with tool, work origin, bed corner, program area and target; a side view
  of the height; or a 3D view. On a Z1 / Z1 Pro (recognised by its 207 × 206 mm travel) both views
  show its bed: a CAD model by cinetronix (CC BY 4.0, converted once to `wwwroot/models/compact-bed.glb`); the top
  view is a picture of the same model. Models are placed with their L-bracket corner on the machine's `anchor1`; the
  height is approximate (bed surface at the lowest Z).
- **Air**: clean the bed 1×, 2× or 3× in a row, or switch the air on and off. The air output depends on the machine,
  recognised by its travel: the Z1's spindle blower (`M811 S100` / `M812`); other
  travels get compressed air (`M7` / `M9`); until the travel is known the air can't be used. A bed cleaning lifts the head to the
  clearance height, sweeps the whole travel there with the air on (lanes along X, from the back to the front,
  `G53 G1` at 3000 mm/min, one move at a time), switches the air off and brings the head back where it was: across
  at the clearance height, then straight down. During a job it first pauses it (`suspend`) and resumes it (`resume`)
  only once the head is exactly back where the program stopped; otherwise the job stays paused. Stop ends the sweep
  early and still returns and resumes. A machine whose pause routine could move the head (`after_suspend_gcode` in
  its config) is not paused for it. The sequence runs on the server, so it completes even if the page is closed;
  disconnecting stops it and waits up to 30 s for the head to return.
- **Console** with history, and an **alarm banner** with Unlock and Reset.

## Start a job

Choose a program on this computer (uploaded to `/sd/gcodes/` when the job starts, replacing a file of that name), in
the machine's programs folder (listed with `ls`) or from the saved programs, set the start options and start it. The
options are those of the machine (remembered per machine in `job-options.json` in the data folder), and the card
shows the exact commands before they are sent:

- **work origin**: keep it, the bed's reference corner (`anchor1`), the head position plus an offset, or probe the
  stock's corner with the XYZ probe block;
- **auto Z probe**: measure the stock's top at an offset from the program's corner (path origin) or the work origin;
  switched on, or with another program chosen, it probes in the middle of the program's area;
- **auto leveling**: probe a grid of 2–15 × 2–15 points over the program's area;
- **scan margin**: trace the program's outline first;
- the machine's **job modes**: on the Z1 auto blow, auto bed clean, anti-static, external output and
  time-lapse (other travels get the firmware's vacuum and external output modes).

Probing asks for the probe first (a manual tool change to confirm in the notifications) and changes back to the tool
that was in the spindle. While a program runs, the job card has **Pause** (`suspend`), **Resume** and **Stop**
(`abort`, confirmed with a second click).

## Saved programs

A chosen program can be saved under a name of its own, with notes and all its start options (work origin, Z probe,
leveling, scan margin, job modes). Saved programs are shared by all machines (job modes a machine doesn't have are
left out when it runs there) and listed under **Saved** in the start card:

- each keeps its own copy of the G-code file in `programs/` in the data folder, named after the original file with a
  GUID before the extension (`my part_<guid>.nc`), so files of the same name can belong to different programs. The
  database (table `saved_programs` in `tinkercnc.db`, one column per option) stores only that file name, so the data
  folder can move. Starting one uploads it under its original file name;
- loading one restores its options exactly as saved; after a change the card offers **Save** (update it) and
  **Save as…** (a new one). Names are unique, ignoring case; saving under a taken name asks first and moves the other
  program to the deleted ones;
- **Replace file** gives a saved program a new version of its G-code and keeps its options (the previous copy is
  deleted);
- deleting (confirmed with a second click) keeps the program and its file: **Show deleted** lists them with
  **Restore** (under a new name when the old one is taken meanwhile) and **Delete forever**;
- the list shows how often each was started and when last (counted once the machine reports it playing).

## Live camera

One upstream connection per machine is shared by every browser tab and served to the page as MJPEG
(`/video/live-<ip>/stream`). Recordings are written to `recordings/*.mjpeg` in the data folder (raw JPEG frames,
which VLC plays; or convert with `ffmpeg -f mjpeg -framerate 10 -i x.mjpeg x.mp4`). Snapshots are saved as `.jpg`.

## Time-lapse

Settings → Time-lapse plays a recording stored on the machine, by path, with pause, resume and seek.

## Job file cache

Downloaded programs are kept in `gcode/` in the data folder, keyed by the MD5 of their content. On reconnect, a
cached copy is used either directly, when the running-file reply contains a known MD5, or after the machine's first
download packet (`B1`, its MD5) matches; the transfer is then cancelled with `B5` before any data is sent.

## Current job

Works on any job, including one started on the machine itself. When the status reports playback, the app:

1. asks the machine which file is running;
2. downloads it (cached by MD5);
3. estimates every line's duration;
4. shows progress, remaining time (scaled by the feed override), the current tool, and a **tool timeline**: one
   colored bar per tool change, with width proportional to its estimated time and a cursor at the current position.
   A job started here with probing (XYZ corner, scan margin, Z probe or auto leveling) has a striped **Probing** part
   before the program, included in the total and the remaining time.

The G-code panel in the right tool bar follows the line being executed; clicking a timeline segment opens it at that
operation. A program chosen to start is shown the same way before it runs.

## Tool-change times

While a job runs, the **Next tool change** tile shows the predicted clock time of the next one and how long until
then (measured or estimated times, scaled by the feed override). Each operation's tooltip on the timeline has its
own: predicted, when it was actually reached, or (no job running) the offset from the start. The axis also shows
when the job will finish. Times use the server's time zone.

## Actual operation times

Every tool operation that is observed from start to finish is timed and stored in `tinkercnc.db` (SQLite) in the
data folder. The next time the same operation runs (in this file or any other), its measured time replaces the
estimate on the timeline (stopwatch icon). This happens whether or not the dashboard is open.

## Notifications

The bell in the right tool bar (red badge for open ones) collects manual tool changes to confirm (sends `M490.2`)
and alarms, whether or not the dashboard is open.

## Operation history

- **Identity:** an operation is the run of lines between two tool changes, identified by `OperationFingerprint`, a
  SHA-256 of its normalized G-code. Comments, `;@MKR` metadata lines and `N` numbers are removed, and numbers and
  case are normalized. The same operation therefore matches across files, renames and reordered toolpaths, while any
  change to its moves gives a new fingerprint.
- **What is timed:** the cutting part only, from the line after `M6` until the line counter leaves the operation.
  Tool changes (which may include waiting for a manual swap) keep their estimate.
- **What gets recorded:**
  - pauses (Hold, Pause, Door) are recorded separately and left out of the cutting time;
  - the time-weighted feed override is stored, and the median is computed on times normalized to 100 %;
  - a run is discarded if it was joined mid-way or hit an alarm, if the line counter jumped backwards, if there was
    a gap of more than 10 s between status reports, or if the start or end can't be pinned to within 2 s.
- **Database:** one row per run in `operation_runs`. Schema changes are tracked with `PRAGMA user_version`
  migrations. Lookups prefer runs from the same machine (by name) and fall back to other machines.
- **On the timeline:** a measured operation's per-line estimates are scaled to add up to its median, so progress
  inside it stays smooth.
- **Probing:** the probing before a job started here (`JobPreparation`) starts with a rough estimate per step (tool
  change to the probe and back, XYZ corner, scan margin, Z probe, leveling points). It is timed from `play` until the
  machine reports a line of the program (the line stays 0 while the queued commands run), minus the estimate of the
  program's lines before that one; waiting for the probe to be inserted counts as paused. It is stored in
  `operation_runs` under `prep:` + a hash of the queued commands and steps, so the same probing (e.g. a saved
  program) gets its measured time the next time. Jobs started elsewhere show no probing part.

## Data folder and settings

Everything the app stores lives in the per-user data folder, never next to the app (an installed app's folder is
replaced by every update): `%APPDATA%\TinkerCNC` on Windows, `~/Library/Application Support/TinkerCNC` on macOS,
`~/.config/TinkerCNC` on Linux. It holds `tinkercnc.db`, `gcode/`, `programs/`, `recordings/`, `machines.json`,
`job-options.json` and, for installed copies, `logs/tinkercnc.log`.

`appsettings.json` (section `TinkerCNC`) can move the whole folder (`DataDirectory`) or single parts
(`HistoryDatabasePath`, `GCodeCacheDirectory`, `SavedProgramsDirectory`, `RecordingsDirectory`, `ManualMachinesPath`,
`JobOptionsPath`), and sets `Window` (open the dashboard in the app's own window), `SourceUrl` (shown under
Settings → About) and the update feed (`Updates:Url`). Every setting can also be given on the command line
(`--TinkerCNC:Window=true`) or as an environment variable (`TinkerCNC__DataDirectory=/data`).
