#!/usr/bin/env bash
# Builds the macOS installer (.pkg), the portable zip and the update packages with Velopack. Run it on a Mac.
#
#   build/pack-macos.sh 1.0.0                 # Apple silicon (osx-arm64)
#   build/pack-macos.sh 1.0.0 osx-x64         # Intel Macs
#
# Signing and notarisation (needed, otherwise Gatekeeper blocks the app) come from the environment:
#   SIGN_APP_IDENTITY="Developer ID Application: …"  SIGN_INSTALL_IDENTITY="Developer ID Installer: …"
#   NOTARY_PROFILE=<keychain profile created with "xcrun notarytool store-credentials">
# BUNDLE_ID overrides the bundle identifier (default com.tinkerdojo.tinkercnc). UPDATES_URL and SOURCE_URL tell installed
# copies where updates come from (a GitHub repository URL or a folder/URL with the files from "vpk pack") and where the
# source lives (Settings → About); the release workflow sets both to the repository.
#
# The .app is assembled here (not by vpk) so its Info.plist can ask for local network access: machine discovery
# uses UDP broadcasts, which macOS blocks until the user allows it. Each architecture is its own update channel.
# Output: artifacts/releases/<runtime>. Not yet run on a Mac: check the first build there.
set -euo pipefail

VERSION=${1:?usage: build/pack-macos.sh <version> [osx-arm64|osx-x64]}
RUNTIME=${2:-osx-arm64}
BUNDLE_ID=${BUNDLE_ID:-com.tinkerdojo.tinkercnc}
ROOT=$(cd "$(dirname "$0")/.." && pwd)
PUBLISH="$ROOT/artifacts/publish/$RUNTIME"
APP="$ROOT/artifacts/bundle/$RUNTIME/TinkerCNC.app"
RELEASES="$ROOT/artifacts/releases/$RUNTIME"
# Own bin/obj folders, so builds of the same source folder elsewhere (IDE, other OS) cannot interfere.
BUILD="$ROOT/artifacts/build/$RUNTIME-on-$(uname -s | tr '[:upper:]' '[:lower:]')"

dotnet publish "$ROOT/src/TinkerCNC.Web/TinkerCNC.Web.csproj" \
  -c Release -r "$RUNTIME" --self-contained -p:Version="$VERSION" -p:DesktopBuild=true \
  -p:UpdatesUrl="${UPDATES_URL:-}" -p:SourceUrl="${SOURCE_URL:-}" \
  --artifacts-path "$BUILD" -o "$PUBLISH"

# Without Blazor's script the dashboard renders but nothing on it reacts: never ship such a build.
if ! grep -q '_framework/blazor.web.js' "$PUBLISH/TinkerCNC.staticwebassets.endpoints.json"; then
  echo "The published build has no _framework/blazor.web.js; delete artifacts/build and try again." >&2
  exit 1
fi

rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp -R "$PUBLISH/." "$APP/Contents/MacOS/"
cp "$ROOT/build/icons/TinkerCNC.icns" "$APP/Contents/Resources/"
cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleIdentifier</key><string>$BUNDLE_ID</string>
  <key>CFBundleName</key><string>TinkerCNC</string>
  <key>CFBundleDisplayName</key><string>TinkerCNC</string>
  <key>CFBundleExecutable</key><string>TinkerCNC</string>
  <key>CFBundleIconFile</key><string>TinkerCNC</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>$VERSION</string>
  <key>CFBundleVersion</key><string>$VERSION</string>
  <key>NSHumanReadableCopyright</key><string>Copyright 2026 TinkerDojo. Licensed under the Apache License 2.0.</string>
  <key>LSMinimumSystemVersion</key><string>12.0</string>
  <key>NSHighResolutionCapable</key><true/>
  <key>NSLocalNetworkUsageDescription</key>
  <string>TinkerCNC finds and connects to CNC machines on your local network.</string>
</dict>
</plist>
PLIST

dotnet tool restore
ARGS=(pack --packId TinkerCNC --packVersion "$VERSION" --packDir "$APP" --mainExe TinkerCNC
      --packTitle "TinkerCNC" --packAuthors "TinkerDojo" --icon "$ROOT/build/icons/TinkerCNC.icns" --bundleId "$BUNDLE_ID"
      --channel "$RUNTIME" --outputDir "$RELEASES")
if [[ -n "${SIGN_APP_IDENTITY:-}" ]]; then
  ARGS+=(--signAppIdentity "$SIGN_APP_IDENTITY" --signInstallIdentity "${SIGN_INSTALL_IDENTITY:?}" --notaryProfile "${NOTARY_PROFILE:?}")
fi
dotnet vpk "${ARGS[@]}"
