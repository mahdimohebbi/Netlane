#!/bin/bash
# Runs the extension self-test and zips the extension for the Chrome Web Store (or "Load unpacked") into dist/.
set -euo pipefail
cd "$(dirname "$0")"

node test/selftest.mjs | tail -n 1
VERSION=$(node -p 'require("./manifest.json").version')
OUT=../dist/Netlane-chrome-extension-$VERSION.zip
mkdir -p ../dist
rm -f "$OUT"
zip -q -r -X "$OUT" manifest.json background.js popup.html popup.js options.html options.js i18n.js ui.css lib icons flags _locales
echo "Built $OUT ($(du -h "$OUT" | cut -f1))"
