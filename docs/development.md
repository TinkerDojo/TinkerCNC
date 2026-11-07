# Development

## Prerequisites

- The [.NET 10 SDK](https://dotnet.microsoft.com/download) (`global.json` pins the major version). Windows, macOS
  and Linux all work for building, testing and running the dashboard as a server; the desktop window needs
  Windows (WebView2) or macOS (WebKit).
- No machine: the simulator below speaks the whole protocol.

## Repository layout

| Path | Contents |
|---|---|
| `src/TinkerCNC.Core` | The protocol library (namespaces `TinkerCNC.*`): discovery, transports, packet framing, typed messages and visitors, file transfer, G-code analysis and time estimates, operation history, setups and the job queue, video clients. No UI, no hosting. |
| `src/TinkerCNC.Host` | Machine services shared by every front end: sessions, discovery service, job control and tracking, bed cleaning, camera and time-lapse feeds, notifications, data paths. No UI and no HTTP. |
| `src/TinkerCNC.Web` | The Blazor Server dashboard, media and toolpath endpoints, the desktop window (Photino) and updates (Velopack). Builds `TinkerCNC.exe` / `TinkerCNC`. |
| `src/TinkerCNC.Cli` | `tinkercnc-cli`: discovery, query, interactive console over LAN or serial. |
| `tools/TinkerCNC.Simulator` | A fake machine: UDP discovery, the framed control protocol, an SD card, job playback, camera and time-lapse WebSockets. |
| `tests/TinkerCNC.Tests` | xUnit tests. |
| `build/` | Installer scripts (`pack-windows.ps1`, `pack-macos.sh`) and the icon generator. |
| `docs/` | This documentation. |

## Build, test, run

```sh
dotnet build TinkerCNC.slnx
dotnet test TinkerCNC.slnx
dotnet run --project src/TinkerCNC.Web                              # http://localhost:5185
dotnet run --project src/TinkerCNC.Web --urls http://0.0.0.0:5185   # reachable from other devices (no login!)
dotnet run --project src/TinkerCNC.Web -- --TinkerCNC:Window=true   # in the app's own window
```

`dotnet run` starts the dashboard in the Development environment (see `Properties/launchSettings.json`). To keep a
development data folder apart from your real one, set `TinkerCNC__DataDirectory=/some/folder` in the environment or
pass `--TinkerCNC:DataDirectory=…`. All settings are listed in [`features.md`](features.md#data-folder-and-settings).

## Simulator

```sh
dotnet run --project tools/TinkerCNC.Simulator -- [--name Simulator] [--ip 127.0.0.1] [--video-port 82]
```

- `--job` starts a demo program at startup (three tools, `;@MKR` metadata header), as if it had been started on the
  machine; the console command `play /sd/gcodes/demo_part.nc` starts it again.
- `--speed 10` sets the playback speed (reported as the feed override, so remaining times stay consistent).
- `--manual-tool-change` makes every `M6` wait in state `Tool` (status `T:active,0.000,target`) until `M490.2`
  arrives, like a machine without a tool changer.
- `--pause-lifts-z` lifts the head 5 mm after `suspend`, like a machine with an `after_suspend_gcode`.
- `--query-path-only` leaves the MD5 out of the running-file reply; `--legacy-line-counter` reports lines like
  firmware before 2026-08-04.
- The text command `alarm` triggers an alarm.

It announces itself on UDP 3333 and speaks the framed protocol on TCP 2222. On the video port it serves both
WebSockets: the live view is a top view of the bed with the tool position, and the time-lapse is a generated
150-frame recording. It takes uploads, lists `/sd/gcodes` (`ls -s`), sets the work offset with `G10 L2`, and runs
`buffer`ed commands when a program starts: `M600` pauses (state `Pause`) until `resume`, `M495` asks for the probe
(with `--manual-tool-change`) and pretends to probe for 2 s, `M6 Tn` asks for that tool. Port 82 needs elevated rights on some systems; the dashboard expects the
camera on port 82, so use the default when you want to see the camera.

## CLI

Installed as a dotnet tool (`dotnet tool install -g TinkerCNC.Cli`, command `tinkercnc-cli`) or run from source:

```sh
dotnet run --project src/TinkerCNC.Cli -- discover 5             # listen for LAN announcements
dotnet run --project src/TinkerCNC.Cli -- query 192.168.1.42     # unicast QUERY
dotnet run --project src/TinkerCNC.Cli -- connect 192.168.1.42   # interactive console (TCP 2222)
dotnet run --project src/TinkerCNC.Cli -- connect-name Workshop_CNC_1234
dotnet run --project src/TinkerCNC.Cli -- serial COM5            # interactive console over USB serial
dotnet run --project src/TinkerCNC.Cli -- ports                  # list serial ports
```

In the interactive console, plain lines are sent as text commands; `/status`, `/watch`, `/raw`, `/hold`, `/resume`,
`/reset` and `/quit` are local.

## Using the library

```csharp
await using var machine = MachineConnection.ForAddress("192.168.1.42");   // port 2222 by default
machine.StatusReceived += s => Console.WriteLine(s);                       // <Idle|MPos:...|WPos:...>
machine.InfoReceived += Console.WriteLine;
machine.MessageReceived += m => m.Accept(myVisitor);                       // typed IMachineMessage + visitor
await machine.ConnectAsync();
await machine.SendCommandAsync("G0 X10 Y10");
await machine.SendRealtimeAsync(RealtimeCommand.FeedHold);
```

Higher-level pieces live in `TinkerCNC.Host`: `MachineSession` (one shared connection with log, travel limits, air
and bed cleaning), `JobControl` (start, pause, stop a program with all options), `JobTracker` (progress, timeline,
measured times), `NotificationCenter`, `CameraService`. `HostServices.AddTinkerCNCHost()` registers them.

## Architecture notes

- **Core → Host → Web.** Core knows the protocol and files, Host knows machines and jobs, Web renders. Nothing in a
  lower layer references a higher one.
- **Typed messages, visitors, no switch.** Every packet type is a `sealed record` implementing `IMachineMessage` or
  `IHostMessage`; consumers implement a visitor (or derive from `MachineMessageVisitor`) and override only what
  they handle. Discriminators map to types through dictionaries. See `CONTRIBUTING.md` for the full rules.
- **Sequences run server-side.** Bed cleaning, job start, operation timing and notifications are host services that
  keep going when the browser tab closes; components subscribe to `Changed` events and re-render.
- **Storage.** One SQLite file (`tinkercnc.db`) with a single ordered migration list in `AppDatabase`; files are
  referenced by name relative to the data folder so the folder can move; deletions are soft.

## Headless UI check

The dashboard can be driven with Playwright (Chromium) against the simulator for screenshots and smoke tests: start
the simulator with `--job`, start the dashboard, open `/`, click **Connect** on the simulated machine, and wait for
the state badge to show `Run`. The screenshots and the demo GIF in `docs/images` were made this way (the GIF from frames of Playwright's video
recording, encoded in the browser with gifenc).
