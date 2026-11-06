<#
.SYNOPSIS
  Builds the Windows installer (Setup.exe), the portable zip and the update packages with Velopack.
.EXAMPLE
  ./build/pack-windows.ps1 -Version 1.0.0
  ./build/pack-windows.ps1 -Version 1.0.1 -SignTemplate 'signtool sign /fd sha256 /tr http://timestamp.digicert.com /td sha256 /a {{file}}'
  ./build/pack-windows.ps1 -Version 1.0.0 -UpdatesUrl https://github.com/<owner>/<repo> -SourceUrl https://github.com/<owner>/<repo>
.PARAMETER UpdatesUrl
  Where installed copies look for updates: a GitHub repository URL or a folder/URL with the files from "vpk pack".
.PARAMETER SourceUrl
  Where the source of this build lives (shown under Settings → About).
.NOTES
  Output: artifacts/releases/win-x64, on the Velopack channel named after the runtime (the release workflow
  downloads and uploads with the same channel). Keep the previous release's files there (or fetch them with
  "dotnet vpk download github --repoUrl …") so Velopack can build a small delta update next to the full package.
  Without signing, Windows SmartScreen warns users the first time they run Setup.exe.
#>
param(
    [Parameter(Mandatory)] [string] $Version,
    [string] $Runtime = 'win-x64',
    [string] $SignTemplate,
    [string] $UpdatesUrl,
    [string] $SourceUrl
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$publish = Join-Path $root "artifacts/publish/$Runtime"
$releases = Join-Path $root "artifacts/releases/$Runtime"
# Own bin/obj folders: the source folder may also be built by Visual Studio or on another OS (WSL), whose restore
# files point to other package folders and would silently drop the framework's web assets.
$host_ = if ([System.Environment]::OSVersion.Platform -eq 'Win32NT') { 'windows' } else { 'unix' }
$build = Join-Path $root "artifacts/build/$Runtime-on-$host_"

dotnet publish (Join-Path $root 'src/TinkerCNC.Web/TinkerCNC.Web.csproj') `
    -c Release -r $Runtime --self-contained -p:Version=$Version -p:DesktopBuild=true `
    -p:UpdatesUrl=$UpdatesUrl -p:SourceUrl=$SourceUrl `
    --artifacts-path $build -o $publish
if ($LASTEXITCODE) { exit $LASTEXITCODE }

# Without Blazor's script the dashboard renders but nothing on it reacts: never ship such a build.
$endpoints = Join-Path $publish 'TinkerCNC.staticwebassets.endpoints.json'
if (-not (Select-String -Path $endpoints -SimpleMatch '_framework/blazor.web.js' -Quiet)) {
    Write-Error "The published build has no _framework/blazor.web.js; delete artifacts/build and try again."
    exit 1
}
# The app is a window app: TinkerCNC.exe must not open a console (PE optional header subsystem 2 = Windows GUI).
$exe = [System.IO.File]::ReadAllBytes((Join-Path $publish 'TinkerCNC.exe'))
$subsystem = [BitConverter]::ToUInt16($exe, [BitConverter]::ToInt32($exe, 0x3c) + 24 + 68)
if ($subsystem -ne 2) {
    Write-Error "TinkerCNC.exe would open a console window (subsystem $subsystem)."
    exit 1
}

dotnet tool restore
$pack = @(
    'pack', '--packId', 'TinkerCNC', '--packVersion', $Version, '--packDir', $publish, '--channel', $Runtime,
    '--mainExe', 'TinkerCNC.exe', '--packTitle', 'TinkerCNC', '--packAuthors', 'TinkerDojo',
    '--icon', (Join-Path $PSScriptRoot 'icons/TinkerCNC.ico'), '--outputDir', $releases,
    '--framework', 'webview2'  # the window's web view; Setup installs it when Windows lacks it
)
if ($SignTemplate) { $pack += @('--signTemplate', $SignTemplate) }
# On Linux/macOS (pwsh) vpk builds for its own OS unless told otherwise.
if ([System.Environment]::OSVersion.Platform -ne 'Win32NT') { $pack = @('[win]') + $pack }
dotnet vpk @pack
exit $LASTEXITCODE
