# TinkerCNC CLI

Command-line companion of [TinkerCNC](https://github.com/TinkerDojo/TinkerCNC), an open-source dashboard for
the Makera Z1 / Z1 Pro desktop CNC.

```sh
dotnet tool install -g TinkerCNC.Cli
tinkercnc-cli discover 5                 # listen for machines announcing themselves on the LAN
tinkercnc-cli query 192.168.1.42         # ask one address who it is
tinkercnc-cli connect 192.168.1.42       # interactive console over Wi-Fi/LAN (TCP 2222)
tinkercnc-cli connect-name Workshop_CNC  # connect by the announced name
tinkercnc-cli serial COM5                # interactive console over USB serial
tinkercnc-cli ports                      # list serial ports
```

In the console, plain lines are sent to the machine as commands; `/status`, `/watch`, `/raw`, `/hold`, `/resume`,
`/reset` and `/quit` are local. TinkerCNC is an independent project, not affiliated with or endorsed by any machine
manufacturer. Licensed under the Apache License 2.0.
