#!/bin/bash
# Builds self-contained single-file Netlane.exe for Windows x64 and ARM64 (works from macOS, Linux or Windows Git Bash)
# and zips each into dist/windows/. Runs the core self-test first.
set -euo pipefail
cd "$(dirname "$0")"

export PATH="$HOME/.dotnet:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
command -v dotnet >/dev/null || { echo "error: .NET 8 SDK not found (https://dot.net)" >&2; exit 1; }

OUT=../dist/windows
rm -rf "$OUT" && mkdir -p "$OUT"

dotnet run --project Netlane.SelfTest -c Release | tail -n 1

for rid in win-x64 win-arm64; do
  dotnet publish Netlane -c Release -r "$rid" --self-contained \
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true \
    -p:DebugType=none -o "obj/publish/$rid" -v quiet -nologo | grep -E "error|warn" || true
  stage="obj/stage/$rid"
  rm -rf "$stage" && mkdir -p "$stage"
  cp "obj/publish/$rid/Netlane.exe" "$stage/Netlane.exe"
  (cd "$stage" && zip -q -X "../../../$OUT/Netlane-$rid.zip" Netlane.exe)
  echo "Built $OUT/Netlane-$rid.zip ($(du -h "$stage/Netlane.exe" | cut -f1) exe)"
done
