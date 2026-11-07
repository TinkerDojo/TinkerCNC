# Machine protocol

The wire protocol of the Makera Z1 / Z1 Pro as implemented in `TinkerCNC.Core`. The Z1 shares its command set with
the open-source Carvera firmware ([CarveraFirmware](https://github.com/MakeraInc/CarveraFirmware), GPL-3.0, a Smoothieware branch);
names in brackets such as *(firmware, Player resume)* point at the source file or module where the behaviour is
defined. TinkerCNC contains no code from it: it is an independent implementation written for interoperability.
Please open an issue when your machine behaves differently.

## Discovery: UDP 3333

- Listen on `0.0.0.0:3333` (shared, so other software can listen too).
- Machines send ASCII datagrams in the form `name,ip,port,busy[,extra]`, at least 4 fields. `busy == "1"` means
  another client is connected.
- To look up one host, send it the unicast payload `QUERY`; it replies in the same format.

## Transports

| Transport | Details |
|---|---|
| Wi-Fi / LAN | TCP. The address is `ip[:port]` and the port defaults to **2222**. |
| USB serial | **115200 8N1**, no flow control |
| Native USB | The Z1 also enumerates as a USB device (ESP32-S3, VID `0x303A` / PID `0x4002`). **Not implemented.** |
| Bluetooth | Used only for Wi-Fi provisioning (BluFi), not for machine control |

## Framing (the same on every transport)

```
86 68 | LEN u16 BE | TYPE u8 | DATA[LEN-3] | CRC u16 BE | 55 AA
LEN = 1 + len(DATA) + 2            total frame = LEN + 6
CRC = CRC-16/XMODEM (poly 0x1021, init 0) over LEN, TYPE, DATA
```

Example: the status query `?` is `86 68 00 04 A1 3F <crc> 55 AA`.

| Dir | Type | Meaning |
|---|---|---|
| → | `A1` | single real-time character (`?` status, `!` hold, `~` resume, `0x18` reset) |
| → | `A2` | text command line, sent as-is with no newline added (G-code, `config-get sd …`, `diagnose`, `wlan -e`, …) |
| → | `B0` | file-transfer command, e.g. `download /sd/config.txt` (no newline) |
| ↔ | `B1`–`B6` | XMODEM-like file transfer: MD5, VIEW, DATA, END, CANCEL, RETRY (see below) |
| ↔ | `B7` | running file: the host sends an empty payload, the machine answers `"<path>\|<md5>"` |
| ← | `81` | status report text `<State\|MPos:x,y,z,a\|WPos:…\|…>` |
| ← | `82` | reply to the diagnose poll |
| ← | `83` / `84` / `85` | load info / load finished / load error |
| ← | `90` | normal info text (command replies) |
| ← | `91` | alarm text, which also carries the "max client connections reached" rejection |

`PacketReader` resynchronises on the `86 68` header, drops frames with a bad length, footer or CRC and counts them.

## Status report

- **State** is the first field: `Idle`, `Run`, `Hold`, `Pause`, `Wait`, `Tool`, `Alarm`, `Door`, …
- **`P` field:** `P:line,percent,elapsed` is present only while a file is playing.
- **`F` field:** `F:a,b,override%`. The third value is the feed override, which scales the remaining time.
- **`T` field:** `T:active,offset[,target]`; `target` is the tool a manual tool change waits for.
- Every other field is passed through unparsed (`StatusFields` knows their names and units).
- **Remaining time:** TinkerCNC estimates every line of the program and reports
  `remaining = time after the current line ÷ (override/100)`.
- **Finding the file:** `0xB7` returns the running file as `"<path>|<md5>"`.
- **G-code metadata:** some CAM exporters write header comments of the form `;@MKR|KIND|key=value|…`, for example
  `;@MKR|TOOL|number=…|name=…|type=…`, `;@MKR|TOOLPATH|number=…|tool_number=…|name=…`,
  `;@MKR|TOOLPATH_START|toolpath_number=…`, `;@MKR|TIME|seconds=…` and `;@MKR|MAXFEEDRATE|value=…`. TinkerCNC reads
  them for tool and toolpath names, the exporter's own time estimate and the rapid rate (`HeaderMetadata`).

## File download (XMODEM-like, big-endian)

```
host    B0 "download <path>"           (no newline)
machine B1 <md5 hex>                   host re-requests with an empty B1 on timeout
host    B2 (empty)                     request the "view"
machine B2 total_packets:u32 packet_size:u16
host    B3 seq:u32                     starting at 1; re-requested when a packet arrives out of order
machine B3 seq:u32 data
…       until seq == total_packets, then host B4
machine B6 = resend last request, B5 = cancel; an alarm aborts the transfer
```

The library pauses status polling while a transfer runs.

## File upload

The same packets as a download with the roles reversed; the machine asks for everything:

```
host    B0 "upload <path>"             (no newline; path characters encoded, see below)
host    B1 <md5 hex>                   may be lost (see below); TinkerCNC sends it again after 1.5 s without a request
machine B2 (empty)                     request the view
host    B2 total_packets:u32 packet_size:u16
machine B3 seq:u32                     starting at 1
host    B3 seq:u32 data                at most 8192 data bytes per packet
…       until the last packet, then machine B4 (file stored)
machine B6 = resend the last reply, B5 = cancel (right away while the machine is moving)
```

The machine empties its receive buffer once it has opened the file, so an MD5 sent right after the command can be
lost; it then asks with `B6` after about 5 s. While it waits for packets it reads nothing else: commands and queries
sent meanwhile are lost, so TinkerCNC runs one transfer, listing or running-file query at a time per machine and
refuses commands while one runs. It cancels (`B5`) an upload it gives up on; the machine itself gives up after about
30 s without progress. It keeps the MD5 next to the file and reports it with the running file (`B7`), so the job
tracker finds an uploaded program in its cache. Files whose name ends in `.lz` are stored compressed and unpacked by
the machine; TinkerCNC uploads plain files *(firmware, Player upload)*.

## Paths in commands

The firmware splits commands at spaces and treats some characters specially, so a path in `play`, `upload`,
`download` or `ls` carries space as `0x01`, `?` as `0x02`, `*` as `0x03`, `!` as `0x04` and `~` as `0x05`; it turns
them back when it reads the path *(firmware, utils shift_parameter)*. Listings return names the same way.

## Listing the SD card

`ls -e -s <dir>` answers with `0x83` packets of lines `name size yyyyMMddHHmmss` (directories end in `/`, names
encoded as above, names starting with a dot are hidden), then `0x84`; a directory that can't be opened answers
`0x85`. `-s` adds size and date, `-e` an end marker *(firmware, SimpleShell ls)*.

## Starting and stopping a program

- `play <path>` starts a program from the SD card when nothing else plays (otherwise it answers `Currently printing,
  abort print first`; a missing file answers `File not found: …`). A machine that has not been homed refuses with an
  alarm (`Machine has not been homed`). `abort` stops a program: the spindle stops and the remaining moves are
  dropped. Pausing is `suspend`/`resume` (below).
- `buffer <command>` queues a command that runs once the next program has started, before its first line. The queue
  is emptied by `abort` during a program and by any alarm or reset (so also by the not-homed refusal), but not by a
  `play` that answers with an error. `play` always begins at the file's first line; the `goto <line>` command jumps
  inside a program only while it is suspended, without restating what earlier lines set *(firmware, Player)*.
- `M600` inside a program (or buffered) does what `suspend` does: the queued moves finish, the position is saved, the
  spindle stops, the pause routine runs if one is configured, and the state turns `Pause` until `resume` (or the
  machine's own button, held). Buffered first, it makes the machine wait right after `play`, before probing and the
  first line: TinkerCNC's queue uses it so a piece can be put in place before every job.
- `G10 L2 P0 X Y` makes that machine position the active work origin (X/Y; Z stays).
- `M495 X Y …` (normally buffered) runs the automation before a program, with X/Y the front left corner of the
  program's cutting moves (rapids such as the move to a safe height are left out) in work coordinates: `C D` (its
  far corner) traces the outline at the clearance height; `O F` probes the stock's top at that offset from the
  corner and makes it Z zero; `A B I J H` probes a grid of I × J points over A × B mm at H mm above the surface and
  follows it (auto leveling); without any of them `P1` goes to the corner first. If another tool than the probe is
  in the spindle, the machine asks for the probe first (on a machine without a tool changer: a manual tool change,
  state `Tool`). `buffer M6 Tn` afterwards changes back to the cutting tool, unless the program changes to a tool of
  its own anyway.
- `M495.3 H D`: with the probe block on the stock's front left corner and the tool above it, the machine touches the
  block from above, the right and the front and makes the stock's corner and top the work origin (H block height,
  default 9 mm; D tool diameter, default 3.175 mm) *(firmware, ATCHandler M495)*.
- **Job modes** are modes the machine keeps, sent on or off before a program: `M331`/`M332` auto vacuum and
  `M331.3`/`M332.3` external output (the output follows the spindle; *firmware, SimpleShell M331*). On the Z1,
  TinkerCNC offers instead `M331.1`/`M332.1` auto blow, `M331.2`/`M332.2` auto bed clean, `M331.4`/`M332.4`
  anti-static, `M951`/`M952` time-lapse and the external output (no vacuum: it clears chips with its blower). The
  Z1-only modes take effect only while a program runs, so their exact behaviour is up to its firmware.

TinkerCNC sends, in this order: the job modes, the work origin (`G10`), the buffered `M600` (queued jobs only),
`M495.3`, `M495` and `M6 Tn`, then `play`, and waits up to 20 s for the status to report the program playing. To
restart a program at a tool operation it uploads a trimmed copy (the modal state at that point, then the lines from
that `M6`), because `play` cannot start later in a file.

## Air (chip blowing)

On machines with a compressed-air output, `M7` switches it on and `M9` off (the firmware's `air` switch, also used as
air assist); its state is only reported in the reply to `diagnose` (field `R`), not in the status report. The
Z1 has no compressed-air output (`M7`/`M9` do nothing): its chips are blown by the blower around the
spindle, which is the spindle fan output, `M811 S<percent>` on and `M812` off; it also cools the spindle.

## Travel limits

The machine homes to the far corner, which becomes machine position 0 (`alpha_max`/`beta_max`/`gamma_max`, 0 when
not set); the soft limits `soft_endstop.x_min`/`y_min`/`z_min` give the other end. `coordinate.anchor1_x`/`_y` is the
bed's reference corner and `coordinate.clearance_z` the height the machine lifts to before crossing the bed.
TinkerCNC reads them with `config-get sd …` after connecting (spaced 200 ms apart, as each query reads `config.txt`
from the SD card) and keeps them for later connections. A Z1 answers X −207…0, Y −206…0, Z −102…0 mm, anchor1 (−190.3, −194.114), clearance Z −3 (the firmware's
built-in defaults are X −371, Y −250, Z −135). TinkerCNC recognises the machine by this travel (`MachineTypes`).

## Pausing a job (suspend / resume)

`suspend` lets the moves already queued finish (state `Wait`), stores the position and pauses (state `Pause`);
commands such as `M7` or jogs then run normally. `resume` returns to the stored position in one straight `G1` move at
1000 mm/min (in work coordinates) and continues, so a host that moved the head in between must bring it back above
that point and lower it first, or the return cuts diagonally through the stock *(firmware, Player resume)*. A
machine can be configured to run G-code right after pausing (`after_suspend_gcode`); `config-get sd
after_suspend_gcode` answers `sd: after_suspend_gcode is set to …` or `… is not in config`. A feed hold (`!`) instead
freezes motion and keeps later commands queued behind the program.

## Manual tool change (confirm on the host)

On machines without an automatic tool changer, `M6 Tn` moves to the tool change position, prints
`Please change the tool to: Tn` and runs `M490.1`: the state becomes **`Tool`** and the status field
`T:active,offset,target` carries the tool it waits for. The host answers with the plain text command **`M490.2`**,
after which the firmware calibrates the tool length and the program continues *(firmware, ATCHandler)*. Reserved
tool numbers: 0 is the probe, 8888 the laser module, 9999 the 3D probe. The dashboard raises a notification when the
state turns `Tool` (red badge on the rail); its Confirm button sends `M490.2`. `M490.1` on its own only sets the
waiting flag (state `Tool`, a beep); the program is held by the tool change script around it, not by the flag, so a
bare `M490.1` is no way to pause a program.

The machine's own button *(firmware, MainButton)*: a short press confirms the tool change in state `Tool`, resumes a
feed hold in `Hold`, and **halts** the machine (an alarm) in `Idle`, `Run` or `Home`; a long press resumes a
suspended program (`Pause`), and in `Idle` does what is configured (repeat the last file from its first line without
any buffered commands, sleep, or a tool change).

## Video: WebSocket on port 82

| Endpoint | Purpose | Protocol |
|---|---|---|
| `ws://<ip>:82/ws_video` | live camera | After connecting, send the text `start_stream`. Each binary message is one image (JPEG). TinkerCNC reconnects after 2 s and warns if no frame arrives within 10 s. |
| `ws://<ip>:82/ws_preview` | time-lapse recordings on the machine | JSON `{"ns":"vpreview","cmd":…,"seq":n}`. Commands: `open`(+`path`), `play`(+`session_id`,`from_frame`), `pause`, `resume`, `stop`. Replies are `rsp:"open"` (`session_id`,`total_frames`,`width`,`height`), `rsp:"meta"` (`total_frames`,`frame_period_us`,`duration_ms`,`first_frame_index`), `play`, `pause`, `eos` and `preempted`; errors are `err` + `message`/`msg`. Frames are binary images. Only one viewer at a time. |

## Limitations and open questions

- **Status poll interval**: 500 ms by default; it can be changed.
- **Status fields**: `P`, `F` and `T` are interpreted; every other field is passed through unparsed.
- **Port for `QUERY`**: 3333 is assumed.
- **The 5th discovery field** and the **payload of type 0x83** are passed through without interpretation.
- **Connect-time setup**: TinkerCNC does not send any initialisation commands (clock, model or version queries) on
  connect.
- **Compressed transfers:** programs are uploaded uncompressed (no `.lz`), which takes longer for big files.
- **Starting programs** follows the open-source firmware and has only run against the simulator. A `play` the machine
  answers with an error (file missing) leaves the buffered start commands queued until the next start or a reset.
- **Time estimates:** they ignore acceleration and assume a fixed 30 s per tool change, so they will differ from the
  real machine. Files with a `;@MKR|TIME` header carry their own total, which the estimate is scaled to match.
  Measured operations replace the estimate once they've run.
- **Accuracy of measured times:** the status line counter may run ahead of actual motion because of the planner
  buffer, and samples arrive every 0.5 s, so each measured operation can be off by about a second. The first
  download of a new file pauses status polling; if it takes longer than 2 s, the first operation of that file isn't
  recorded on that run.
- **Native USB transport**: not implemented. USB-serial and TCP are.
- **Hardware coverage**: developed against the simulator; connecting, status, console commands and the air output
  were checked on one Z1. The Z1 Pro has not been tried. The simulator follows this document
  and cannot show where real firmware behaves differently.
- **Recording paths**: the time-lapse page doesn't list the machine's SD card, so you have to type the path of a
  recording yourself.
- **Live camera**: whether it accepts more than one viewer at a time is unknown. The app opens a single upstream
  connection anyway.
