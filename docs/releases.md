# Installers, updates and releases

Releases are built with [Velopack](https://velopack.io) (`vpk` is pinned in `dotnet-tools.json`).

## Building installers by hand

```powershell
./build/pack-windows.ps1 -Version 1.0.0 -UpdatesUrl https://github.com/<owner>/<repo> -SourceUrl https://github.com/<owner>/<repo>
```
```sh
UPDATES_URL=https://github.com/<owner>/<repo> SOURCE_URL=https://github.com/<owner>/<repo> \
  build/pack-macos.sh 1.0.0 [osx-arm64|osx-x64]      # on a Mac
```

Each run publishes a self-contained build (no .NET install needed) and writes to `artifacts/releases/<runtime>`:
`TinkerCNC-win-x64-Setup.exe` / a `.pkg`, a portable zip, the `*-full.nupkg` update package and the release index
(`releases.<channel>.json`). With the previous release's files in that folder (`dotnet vpk download github …`)
Velopack also builds a small delta package.

`UpdatesUrl` and `SourceUrl` are baked into the build: installed copies look for updates at `UpdatesUrl` (a GitHub
repository URL, or any folder/URL holding the files from `vpk pack`) and show `SourceUrl` under Settings → About.
Values in `appsettings.json` (`TinkerCNC:Updates:Url`, `TinkerCNC:SourceUrl`) override them.

## The installed app

- Installs per user without admin rights (Windows: `%LocalAppData%\TinkerCNC`, Start menu and desktop shortcut).
- A window app (Photino: WebView2 on Windows, which Setup installs if missing; WebKit on macOS) with the server
  running inside it: no console, no browser tab. Closing the window quits the app; starting it again brings the open
  window to the front. The log is written to `logs/tinkercnc.log` in the data folder.
- `TinkerCNC:Window = false` opens the browser instead; builds started from source run as a plain server unless
  started with `--TinkerCNC:Window=true`.
- Settings → About shows the version, checks the feed and installs an update with a restart.

## Signing

Unsigned builds work, but Windows SmartScreen warns on first start ("More info → Run anyway") and macOS Gatekeeper
blocks the `.pkg` until the user allows it in System Settings → Privacy & Security.

- Windows: `-SignTemplate 'signtool sign /fd sha256 /tr http://timestamp.digicert.com /td sha256 /a {{file}}'`.
- macOS: set `SIGN_APP_IDENTITY`, `SIGN_INSTALL_IDENTITY` and `NOTARY_PROFILE` (Apple Developer ID and notarisation,
  `xcrun notarytool store-credentials`). The macOS `Info.plist` asks for local network access, which machine
  discovery needs.

## Releasing from GitHub

Pushing a tag `vX.Y.Z` runs `.github/workflows/release.yml`: it builds the Windows installer and both macOS
packages, bakes the repository URL in as update feed and source link, and uploads everything to the GitHub release
of that tag (`dotnet vpk upload github`). Installed copies then update from the release feed.

```sh
git tag v0.3.1
git push origin v0.3.1
```

The workflow does not sign: add the signing steps and secrets once you have certificates. It was written against
vpk 1.2.x; if an option is rejected, check `dotnet vpk upload github --help`.

## Container image

The `Dockerfile` builds the dashboard as a server image (no window) for `linux/amd64` and `linux/arm64`; the release
workflow pushes it to `ghcr.io/<owner>/<repo>` with the version and `latest` tags (a new GHCR package is private
until you make it public in its package settings). Run it with host networking so machine discovery hears the UDP
broadcasts, and keep the data folder in a volume:

```sh
docker run -d --name tinkercnc --network host -v tinkercnc-data:/data ghcr.io/tinkerdojo/tinkercnc:latest
```

`compose.yaml` holds the same setup. Without host networking, publish the port (`-p 5185:5185`) and add machines by
address. The dashboard has no login, and with host networking it serves the whole LAN. To build locally:
`docker build -t tinkercnc .`.

## NuGet packages

`TinkerCNC.Core` (the protocol library) and `TinkerCNC.Cli` (a dotnet tool) are packed by the release workflow with
the tag's version. They are pushed to nuget.org when the repository secret `NUGET_API_KEY` is set and otherwise only
attached to the workflow run. Pack locally with `dotnet pack src/TinkerCNC.Core -c Release -o artifacts/nuget`.

## Icon

`build/make-icons.py` draws the icon (`.ico`, `.icns`, favicon) without any image library. Run it again after
changing the colours or the shape.
