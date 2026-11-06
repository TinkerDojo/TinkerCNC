# Third-party notices

TinkerCNC includes or depends on the following open-source components. Each remains under its own license, which
permits redistribution together with this Apache-2.0 licensed software.

## Shipped with the application

| Component | Used for | License | Copyright |
|---|---|---|---|
| [three.js](https://threejs.org) r170 incl. OrbitControls, GLTFLoader (`src/TinkerCNC.Web/wwwroot/lib/three`) | 3D views | MIT, see `wwwroot/lib/three/LICENSE` | © 2010-2024 three.js authors |
| [.NET runtime and ASP.NET Core](https://github.com/dotnet) | runtime, web server, Blazor | MIT | © .NET Foundation and Contributors |
| [Microsoft.Data.Sqlite](https://github.com/dotnet/efcore) | operation history and saved programs | MIT | © Microsoft Corporation |
| [SQLitePCLRaw](https://github.com/ericsink/SQLitePCL.raw) | SQLite native bindings | Apache-2.0 | © 2014-2024 SourceGear, LLC |
| [SQLite](https://sqlite.org) (via SQLitePCLRaw) | database engine | Public domain | — |
| [System.IO.Ports](https://github.com/dotnet/runtime) | USB-serial connection | MIT | © Microsoft Corporation |
| [Microsoft.Extensions.*](https://github.com/dotnet/runtime) | configuration, hosting, logging | MIT | © Microsoft Corporation |
| [Velopack](https://velopack.io) | installer and updates | MIT | © Velopack Ltd. |
| [Photino.NET](https://www.tryphotino.io) / Photino.Native | the app window | Apache-2.0 | © TryPhotino |
| [Microsoft Edge WebView2](https://developer.microsoft.com/microsoft-edge/webview2/) loader (Windows) | web view in the window | BSD-3-Clause (loader); the runtime is part of Windows | © Microsoft Corporation |

## Models

| Model | Used for | License | Author |
|---|---|---|---|
| [Makera Z1 bed CAD](https://github.com/cinetronixlabs/makera-z1-bed-cad-files) v1.1, converted to `src/TinkerCNC.Web/wwwroot/models/compact-bed.glb` | bed in the machine views | [CC BY 4.0](https://creativecommons.org/licenses/by/4.0/); converted (aluminium base, MDF bed and L-bracket selected, recoloured, tessellated) | cinetronix. Its repository states that Makera approved the distribution of these reference files. |

## Development only (not shipped)

| Component | Used for | License |
|---|---|---|
| [SkiaSharp](https://github.com/mono/SkiaSharp) | simulator camera images | MIT |
| [xUnit](https://xunit.net), xunit.runner.visualstudio | tests | Apache-2.0 |
| [coverlet](https://github.com/coverlet-coverage/coverlet) | test coverage | MIT |
| Microsoft.NET.Test.Sdk | tests | MIT |
| [vpk](https://velopack.io) (`dotnet-tools.json`) | building installers | MIT |

Full license texts: MIT — <https://opensource.org/license/mit>, Apache-2.0 — <https://www.apache.org/licenses/LICENSE-2.0>,
BSD-3-Clause — <https://opensource.org/license/bsd-3-clause>.
