#!/bin/bash
# Builds Netlane.app (with embedded widget) and packages dist/Netlane.dmg.
# Uses xcodebuild when full Xcode is installed; otherwise falls back to swiftc + the Command Line Tools SDK.
set -euo pipefail
cd "$(dirname "$0")"

APP_NAME=Netlane
DIST=dist
APP="$DIST/$APP_NAME.app"
DMG="$DIST/$APP_NAME.dmg"
MIN_OS=14.0

command -v xcodegen >/dev/null || brew install xcodegen
xcodegen generate --quiet

rm -rf "$DIST"
mkdir -p "$DIST"

if [[ -z "${TEAM_ID:-}" ]]; then
  echo "warning: TEAM_ID not set — signing ad-hoc. The widget may not appear in the widget gallery." >&2
fi

if xcodebuild -version >/dev/null 2>&1; then
  if [[ -n "${TEAM_ID:-}" ]]; then
    SIGN_ARGS=(CODE_SIGN_STYLE=Automatic DEVELOPMENT_TEAM="$TEAM_ID" -allowProvisioningUpdates)
  else
    SIGN_ARGS=(CODE_SIGN_IDENTITY="-" CODE_SIGN_STYLE=Manual DEVELOPMENT_TEAM="" PROVISIONING_PROFILE_SPECIFIER="")
  fi
  xcodebuild -scheme "$APP_NAME" -configuration Release -derivedDataPath build \
    -destination 'generic/platform=macOS' ARCHS="arm64 x86_64" ONLY_ACTIVE_ARCH=NO -quiet "${SIGN_ARGS[@]}"
  ditto "build/Build/Products/Release/$APP_NAME.app" "$APP"
else
  echo "note: full Xcode not found — building with swiftc (Command Line Tools)." >&2
  SDK=$(xcrun --show-sdk-path)
  OBJ=build/swiftc
  rm -rf "$OBJ"; mkdir -p "$OBJ"
  compile() { # out, extra flags..., sources via SOURCES
    local out=$1; shift
    for arch in arm64 x86_64; do
      xcrun swiftc -O -swift-version 5 -sdk "$SDK" -target "$arch-apple-macos$MIN_OS" -parse-as-library \
        "$@" "${SOURCES[@]}" -o "$OBJ/$(basename "$out")-$arch"
    done
    lipo -create "$OBJ/$(basename "$out")-arm64" "$OBJ/$(basename "$out")-x86_64" -output "$out"
  }
  APPEX="$APP/Contents/PlugIns/${APP_NAME}Widget.appex"
  mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources" "$APPEX/Contents/MacOS"
  cp App/Info.plist "$APP/Contents/Info.plist"
  cp Resources/AppIcon.icns "$APP/Contents/Resources/AppIcon.icns"
  cp Widget/Info.plist "$APPEX/Contents/Info.plist"
  printf 'APPL????' > "$APP/Contents/PkgInfo"
  SOURCES=(App/*.swift Shared/*.swift)
  compile "$APP/Contents/MacOS/$APP_NAME" -module-name "$APP_NAME" -Xlinker -rpath -Xlinker @executable_path/../Frameworks
  SOURCES=(Widget/*.swift Shared/*.swift)
  compile "$APPEX/Contents/MacOS/${APP_NAME}Widget" -module-name "${APP_NAME}Widget" -application-extension \
    -framework WidgetKit -framework SwiftUI -Xlinker -e -Xlinker _NSExtensionMain -Xlinker -rpath -Xlinker @executable_path/../../../../Frameworks

  if [[ -n "${TEAM_ID:-}" ]]; then
    IDENTITY=$(security find-identity -v -p codesigning | grep "($TEAM_ID)" | head -n1 | sed -E 's/.*"(.*)"/\1/')
    [[ -n "$IDENTITY" ]] || { echo "error: no signing identity for team $TEAM_ID" >&2; exit 1; }
    SIGN_FLAGS=(--options runtime --timestamp)
  else
    IDENTITY="-"
    SIGN_FLAGS=(--options runtime)
  fi
  codesign --force --sign "$IDENTITY" "${SIGN_FLAGS[@]}" --entitlements Widget/NetlaneWidget.entitlements "$APPEX"
  codesign --force --sign "$IDENTITY" "${SIGN_FLAGS[@]}" --entitlements App/Netlane.entitlements "$APP"
fi

codesign --verify --deep --strict "$APP"

STAGE=build/dmg
rm -rf "$STAGE"; mkdir -p "$STAGE"
ditto "$APP" "$STAGE/$APP_NAME.app"
ln -s /Applications "$STAGE/Applications"
hdiutil create -quiet -volname "$APP_NAME" -srcfolder "$STAGE" -fs HFS+ -format UDZO -ov "$DMG"
echo "Built $APP and $DMG"
