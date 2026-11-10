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
  height is approximate (bed surface at the lowest Z). The 3D views (this one and the toolpath) are navigated like
  Autodesk Fusion: the middle button drags the view, Shift + middle button orbits (sideways turns about the
  vertical, up and down tilts, never past the poles) about the point under the cursor, the wheel zooms towards the
  cursor. The left button orbits too; on a touch screen one finger orbits, two fingers drag and pinch.
- **Toolpath**: the chosen program's toolpath in 3D, coloured by tool, in the machine's space: once the travel and
  the work origin are known, the machine (travel, bed or its model, clearance plane) is drawn around it and the
  toolpath and the tool sit at the work origin, where the program really cuts. Heights are those of the tool's tip:
  the firmware reports `WPos = MPos − work offset − tool length offset` (the offset is the second value of the
  status field `T`, set by the tool length calibration), so the tip is `MPos` lowered by that offset and work zero
  is `MPos − WPos` without it. The machine view draws the tool, work origin and Go-to target the same way, and the
  program area as the box around its cutting moves (rapids and safe heights left out). A start that probes Z zero
  (auto Z probe, XYZ probe block) sets it only when the job starts; until then both views draw the program at the
  machine's current Z zero and say so.
  Both views draw the stock as a translucent block under the cutting area, from the bed up to work Z0 (the stock's
  probed top), and give its height. The machine reports no bed height (none of its `coordinate.*`, `zprobe.*` or
  `atc.*` settings is one), so the bed is drawn at the lowest Z the spindle reaches: when the stated height is more
  than the real stock's, the real bed is higher by the difference.
  **Live** draws the moves executed so far, **Preview** the whole toolpath as it is once the job has run; without a
  choice it shows Live while a job runs and Preview otherwise. Iso / Top / Front frame the toolpath from there, Fit
  frames it from the current direction, Machine shows the whole machine. G0 shows or hides the rapid moves.
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
- **record a time-lapse**: from the moment the machine plays until it stops, the camera records one frame every
  second into a `<program>_timelapse_<time>.mp4` file in `recordings/` in the data folder, which plays 30 times
  faster than the job (a one-hour job becomes a two-minute clip). The camera is started if it is off; a recording
  that is already running is left alone. Nothing is sent to the machine for it (its own `M951` time-lapse is not used);
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

**Start** on the **Current job** card makes the chosen program the queue's first entry and starts the queue: the job
runs at once with the options as set on the card (without waiting for the go: whoever pressed Start is at the
machine), as the setup's file it was loaded from or as a copy of the file. Its × count can be raised while it runs;
every further run, and the entries after it, wait for the go as described below. A start the machine refuses leaves no
entry behind. A job whose work origin is "where the head is now" runs only once: a further run would find the head
wherever the previous one ended.

**Start queue** also works while the machine runs a job the queue did not start (one started on the machine itself): that job counts as the queue's first, and the queue goes on once it has finished. If it does not finish
(an alarm, Stop, the connection lost, ended before its last operation), the queue stops instead. **Stop queue** while
the queue waits for that job only cancels the queue; the job goes on.

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

## Resume after a stop

While a job runs, where it is (program, line, work zero) is kept per machine in `tinkercnc.db` (table
`job_snapshots`): every 30 s, and at once with the last line seen when the job ends early, whether by an emergency
stop or alarm, Stop, or the connection lost (the machine switched off). The program itself stays in the program
cache. A job that runs to its end leaves nothing behind. So after unlocking or switching the machine back on, also
after closing and reopening the app, the **Current job** card offers **Resume at line N** (with how far it got and
in which operation). It loads a copy of the program that brings the machine back to that line first:

1. the work origin as it was when the job stopped (`G10 L2 P0`), the plane and work offset;
2. the tool of that line (`M6 T…`): the machine asks for it and measures its length again, which it forgets when
   switched off;
3. up to the clearance height (`G53 G0 Z…`), across to where the tool was before that line, spindle and air as they
   were, a pause for the spindle to reach speed, then down to that point at the program's feed;
4. the program's units, absolute or incremental moves, motion mode and feed, then the program from that line on.

The card shows these lines before the start; probing and leveling are off and the work origin is kept. A job
started with auto leveling resumes without its height map (the card says so). A machine that was switched off has
to be homed first. Started, the resume is the queue's first entry like any job; further runs of it run the whole
file. **Dismiss** forgets the stopped job.

## Live camera
## Live camera

One upstream connection per machine is shared by every browser tab and served to the page as MJPEG
(`/video/live-<ip>/stream`). Recordings are written to `recordings/*.mp4` in the data folder as H.264 video that
every player and browser opens; the links on the camera card play them in a new tab. Snapshots are saved as `.jpg`.

The video is encoded by TinkerCNC itself, without ffmpeg or other native codecs: Constrained Baseline H.264 with a
fixed quality (QP 26 for P pictures, 24 for I pictures), a keyframe every 30 pictures, and P pictures that copy
unchanged blocks from the previous picture and code only the difference of the rest, which suits a camera on a
tripod. A manual recording keeps every fifth camera frame, shown at the time it arrived (it plays in real time at a
fifth of the camera's frame rate and data); a time-lapse keeps one frame a second and plays at 30 frames a second.
Encoding runs on a background task; if it falls behind the camera, frames are dropped rather than queued. While a
recording runs, the file is `<name>.mp4.part` with a small journal (`.idx`) next to it; the part file is closed and
reopened every two seconds, so the size Windows Explorer shows grows with it. A recording the app did not get to
finish (the app was closed, the computer lost power) is completed with the frames it has at the next start.
Recordings made by versions before 0.3.0 (`.mjpeg`, raw JPEG frames) stay where they are and still download from the
camera card.

## Time-lapse

Settings → Time-lapse plays a recording stored on the machine, by path, with pause, resume and seek. Time-lapses
of jobs started here are recorded from the camera into the recordings folder (see [Start a job](#start-a-job)) and
open in the browser like any recording.

## Job file cache

Downloaded programs are kept in `gcode/` in the data folder, keyed by the MD5 of their content. On reconnect, a
cached copy is used either directly, when the running-file reply contains a known MD5, or after the machine's first
download packet (`B1`, its MD5) matches; the transfer is then cancelled with `B5` before any data is sent.

The same works the other way round: before a file from this computer, a setup or the queue is uploaded, the machine
is asked for the file of that name (`download`, then `B5` right after its `B1`). When the MD5 it announces is the
file's own, the upload is skipped, so running a program again starts at once. A missing file, a file without an
announced MD5 or no quick answer simply means the file is uploaded.

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

**Settings → Notifications** also shows them as the system's own notifications (the browser asks for permission
once), for every machine and on any page, even while the dashboard is in the background. Ones that wait for an
answer stay until clicked, a click opens the machine's page, and they disappear once answered or resolved on the
machine. The choice is remembered per browser. Browsers offer notifications only while a dashboard tab or window is
open, and only to secure pages: HTTPS, or the dashboard on this computer (`localhost`); opened by IP address from
another computer, the setting explains that it is unavailable.

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

## Language

The dashboard is available in English, German, French and Italian. It follows the browser's language (in the app's
own window, the system's) and falls back to English; **Settings → Language** picks one for this browser instead,
remembered in a cookie. Words the machine reports itself (alarm texts, console traffic) are shown as they come.

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
