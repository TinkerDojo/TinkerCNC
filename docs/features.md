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

The **Job** card on a machine's page shows the job that is running, or the program chosen to run next, or offers a
program: one of a setup's files, one in the machine's programs folder (listed with `ls`) or a file from this
computer (uploaded to `/sd/gcodes/` when the job starts, replacing a file of that name). The program is set up in
one place: the options on the left, and on the right what starting will do, step by step, the estimated time, why
it can't start yet, the **Start job** button and the exact commands. The options are those of the machine
(remembered per machine in `job-options.json` in the data folder), or of the setup the file was loaded from:

- **work origin**: keep it, the bed's reference corner (`anchor1`), the head position plus an offset, or probe the
  stock's corner with the XYZ probe block;
- **auto Z probe**: measure the stock's top at an offset from the program's corner (path origin) or the work origin;
  switched on, or with another program chosen, it probes in the middle of the program's area;
- **auto leveling**: probe a grid of 2–15 × 2–15 points over the program's area;
- **scan margin**: trace the program's outline first;
- **record a time-lapse**: from the moment the machine plays until it stops, the camera records one frame every few
  seconds (the interval follows the estimated length, aiming at about 300 frames) into a `<program>_timelapse_<time>.mjpeg`
  file in `recordings/` in the data folder. The camera is started if it is off; a recording that is already running
  is left alone. Nothing is sent to the machine for it (its own `M951` time-lapse is not used);
- the machine's **job modes**: on the Z1 auto blow, auto bed clean, anti-static and external output (other travels
  get the firmware's vacuum and external output modes).

Probing asks for the probe first (a manual tool change to confirm in the notifications) and changes back to the tool
that was in the spindle. While a program runs, the card shows the time left, the progress, the tool, the next tool
change and the feed override, with **Pause** (`suspend`), **Resume** and **Stop** (`abort`, confirmed with a second
click).

**Start from** a tool operation: a program with several tool changes can begin at any of them, which is how a job
that ended early (Stop, an alarm, a lost connection) is taken up again without regenerating the G-code. The
firmware plays a file from its first line only, so TinkerCNC uploads a trimmed copy named `<file>_from_T6.nc`: a
comment, the modal state the program was in at that point (units, `G90`/`G91`, plane, work offset, feed, spindle,
`M7`/`M8`), then the lines from that operation's `M6` to the end. The machine asks for the tool as usual, or carries
on if it is in the spindle already. Choosing an operation switches scan margin, Z probe and leveling off and keeps the
work origin, since the piece has not moved; they can be switched back on. After a job ends early, the operation it
was in is marked in the list, and the Job card's empty state keeps the **last job** with **Run again** and **Run
again from T…** (also for a job started on the machine itself, from the file downloaded for the timeline).

## Setups

A **setup** is a named set of start options (work origin, Z probe, leveling, scan margin, time-lapse, job modes)
with notes and the G-code files that run with it, shared by all machines (job modes a machine doesn't have are left
out when it runs there). One setup, many files: the same job cut with a different file per piece. Setups are picked
under **Setups** when choosing a program on a machine's page (click a file), and managed there or on the
**Setups** page (which also creates a setup with the default options and its first files):

- each file keeps its own copy in `programs/` in the data folder, named after the original file with a GUID before
  the extension (`my part_<guid>.nc`), so files of the same name can belong to different setups or the same one. The
  database (tables `setups` and `setup_files` in `tinkercnc.db`, one column per option) stores only that file name,
  so the data folder can move. Starting a file uploads it under its original name;
- loading a file restores the setup's options exactly as saved; after a change the card offers **Save** (update the
  setup) and **Save…** (a dialog: add the file to an existing setup, optionally with the options as set, or create a
  new setup with these options and this file as its first). Names are unique, ignoring case; a taken name asks first
  and moves the other setup to the deleted ones;
- **Manage** on a setup renames it, changes its notes, adds files, and on each file replaces its G-code (the options
  stay, the previous copy is deleted) or deletes it; **Delete…** moves the setup with its files to the deleted ones;
- deleting (confirmed with a second click) keeps everything: **Show deleted** lists deleted setups and files with
  **Restore** (a setup under a new name when the old one is taken meanwhile) and **Delete forever**;
- the list shows how often each setup and each file was started and when last (counted once the machine reports it
  playing), and the setup's options in a few words. Options are edited on a machine's page, where the machine's own
  job modes are known.

Saved programs of earlier versions become one setup with one file each when the database is migrated.

## Job queue

The **Queue** card on a machine's page runs jobs one after the other without a trip to the computer in between:
every entry is a file run with the options of its setup (as they are when it starts), a number of times. **Add…**
takes a setup, any of its files, and files from this computer, which are queued for these runs only (a copy in
`queue/` in the data folder that goes when the entry is done; **Keep in setup** makes it one of the setup's files).
Entries can be reordered, repeated and removed while the queue runs; the running one stays. The queue is stored per
machine (table `queue_entries`), so it survives a restart of the app, stopped.

Every queued job, the first included, starts with a pause: `M600` is queued with `buffer` before the probing, so the
machine waits right after `play`, before anything moves, in state `Pause`. Put the piece in place, then hold the
machine's button until it continues (its resume while paused) or press **Resume** on the dashboard or in the
notification; probing then asks for the probe as usual (a short press confirms). The next entry starts as soon as the
machine is idle again. Beware that a short press of the button while the machine is Idle or Run halts it.

A setup whose work origin is "where the head is now" can't be queued: in a queue the head is wherever the previous
job ended. A Z probe point outside a file's cutting area is moved to the middle of that area, noted in the log.

The queue stops when a job fails: an alarm, Stop (also **Stop queue**, which stops the running job too, confirmed
with a second click), a start the machine refused, the connection lost, or a job that ended before its last
operation. The entry stays, marked failed with the reason, with **Retry** from the beginning or from a tool operation
(the interrupted one is preselected; the retry uploads the trimmed copy described under **Start from**) and
**Remove**; **Start queue** then goes on. Finished entries drop off and count towards "n of m jobs done"; the ×
button clears the queue and the count.

## Live camera
## Live camera

One upstream connection per machine is shared by every browser tab and served to the page as MJPEG
(`/video/live-<ip>/stream`). Recordings are written to `recordings/*.mjpeg` in the data folder (raw JPEG frames,
which VLC plays; or convert with `ffmpeg -f mjpeg -framerate 10 -i x.mjpeg x.mp4`). Snapshots are saved as `.jpg`.

## Time-lapse

Settings → Time-lapse plays a recording stored on the machine, by path, with pause, resume and seek. Time-lapses
of jobs started here are recorded from the camera into the recordings folder (see [Start a job](#start-a-job)) and
open in the browser like any recording.

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

The bell in the right tool bar (red badge for open ones) collects manual tool changes to confirm (sends `M490.2`),
alarms, and the queue's messages: a job waiting for the go (with **Resume**), the queue stopped with the reason, and
the queue finished; whether or not the dashboard is open.

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
`~/.config/TinkerCNC` on Linux. It holds `tinkercnc.db`, `gcode/`, `programs/`, `queue/`, `recordings/`,
`machines.json`, `job-options.json` and, for installed copies, `logs/tinkercnc.log`.

`appsettings.json` (section `TinkerCNC`) can move the whole folder (`DataDirectory`) or single parts
(`HistoryDatabasePath`, `GCodeCacheDirectory`, `SetupFilesDirectory`, `QueuedFilesDirectory`, `RecordingsDirectory`,
`ManualMachinesPath`, `JobOptionsPath`), and sets `Window` (open the dashboard in the app's own window), `SourceUrl` (shown under
Settings → About) and the update feed (`Updates:Url`). Every setting can also be given on the command line
(`--TinkerCNC:Window=true`) or as an environment variable (`TinkerCNC__DataDirectory=/data`).
