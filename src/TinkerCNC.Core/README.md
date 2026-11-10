# TinkerCNC.Core

The protocol library behind [TinkerCNC](https://github.com/TinkerDojo/TinkerCNC), an open-source dashboard for
the Makera Z1 / Z1 Pro desktop CNC. It has no UI and no hosting dependencies.

- UDP discovery (port 3333) and the unicast `QUERY` probe
- TCP (port 2222) and USB-serial transports with framed packets (`0x8668` header, CRC-16/XMODEM)
- Typed messages for every packet kind, dispatched through visitors
- File upload and download (the XMODEM-like `B1`–`B6` exchange), SD-card listings, the running-file query
- Job start: work origin, XYZ probe block, auto Z probe, auto leveling, scan margin, job modes
- G-code analysis: per-line time estimates, tool segments, header metadata, operation fingerprints and history
- Camera (MJPEG over WebSocket) and time-lapse clients, and an H.264/MP4 writer for recordings (managed code, no native codec)

```csharp
await using var machine = MachineConnection.ForAddress("192.168.1.42");   // port 2222 by default
machine.StatusReceived += s => Console.WriteLine(s);                       // <Idle|MPos:...|WPos:...>
machine.MessageReceived += m => m.Accept(myVisitor);                       // typed IMachineMessage + visitor
await machine.ConnectAsync();
await machine.SendCommandAsync("G0 X10 Y10");
```

The protocol is documented in the repository under `docs/protocol.md`. TinkerCNC is an independent project, not
affiliated with or endorsed by any machine manufacturer; product names are used only to identify compatible
machines. Licensed under the Apache License 2.0.
