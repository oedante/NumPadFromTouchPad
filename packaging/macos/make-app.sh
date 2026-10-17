#!/usr/bin/env bash
# Собирает OgnelissNumTouch.app из результата dotnet publish (osx-arm64 / osx-x64) и подписывает ad-hoc.
# Для распространения нужна подпись Developer ID и нотаризация (см. BUILD.md).
set -euo pipefail
rid="${1:-osx-arm64}"; root="$(cd "$(dirname "$0")/../.." && pwd)"
src="$root/artifacts/$rid"; app="$root/artifacts/OgnelissNumTouch-$rid.app"
rm -rf "$app"; mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
cp "$src/OgnelissNumTouch" "$app/Contents/MacOS/"
cp "$root/src/Ogneliss.NumTouch.App/Assets/tray_on.png" "$app/Contents/Resources/"
cat > "$app/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
  <key>CFBundleName</key><string>Ogneliss NumTouch</string>
  <key>CFBundleIdentifier</key><string>ru.ogneliss.numtouch</string>
  <key>CFBundleExecutable</key><string>OgnelissNumTouch</string>
  <key>CFBundleVersion</key><string>3.0.0</string>
  <key>CFBundleShortVersionString</key><string>3.0.0</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>LSMinimumSystemVersion</key><string>12.0</string>
  <key>NSHighResolutionCapable</key><true/>
  <key>LSUIElement</key><false/>
</dict></plist>
PLIST
codesign --force --deep --sign - "$app" || echo "codesign недоступен — пакет не подписан"
(cd "$root/artifacts" && ditto -c -k --keepParent "OgnelissNumTouch-$rid.app" "OgnelissNumTouch-$rid.zip")
echo "$app"
