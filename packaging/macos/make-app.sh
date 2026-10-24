#!/usr/bin/env bash
# Собирает OgnelissNumTouch-<rid>.app и .zip из результата dotnet publish (osx-arm64 / osx-x64).
#
# Подпись:
#   MACOS_SIGN_IDENTITY не задана → ad-hoc (запуск только через «Открыть» в контекстном меню);
#   MACOS_SIGN_IDENTITY="Developer ID Application: Имя Фамилия (TEAMID)" → подпись Developer ID с hardened runtime
#   и меткой времени (сертификат должен быть в связке ключей). Затем — packaging/macos/notarize.sh.
set -euo pipefail
rid="${1:-osx-arm64}"; root="$(cd "$(dirname "$0")/../.." && pwd)"
src="$root/artifacts/$rid"; app="$root/artifacts/OgnelissNumTouch-$rid.app"
version="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$root/src/Ogneliss.NumTouch.App/Ogneliss.NumTouch.App.csproj" | head -1)"
short="${version%%-*}"
rm -rf "$app"; mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
cp -R "$src/." "$app/Contents/MacOS/"
cp "$root/packaging/macos/AppIcon.icns" "$app/Contents/Resources/"
cp "$root/LICENSE" "$root/NOTICE" "$app/Contents/Resources/"   # Apache 2.0: лицензия и указание автора
# В Contents/MacOS должен быть только код (Mach-O): символы отладки библиотек проекта не нужны и мешают подписи.
find "$app/Contents/MacOS" -name "*.pdb" -delete
cat > "$app/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
  <key>CFBundleName</key><string>Ogneliss NumTouch</string>
  <key>CFBundleDisplayName</key><string>Ogneliss NumTouch</string>
  <key>CFBundleIdentifier</key><string>ru.ogneliss.numtouch</string>
  <key>CFBundleExecutable</key><string>OgnelissNumTouch</string>
  <key>CFBundleIconFile</key><string>AppIcon</string>
  <key>CFBundleVersion</key><string>$short</string>
  <key>CFBundleShortVersionString</key><string>$short</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>LSMinimumSystemVersion</key><string>12.0</string>
  <key>NSHighResolutionCapable</key><true/>
  <key>LSUIElement</key><false/>
</dict></plist>
PLIST

if [ -n "${MACOS_SIGN_IDENTITY:-}" ]; then
  ent="$root/packaging/macos/entitlements.plist"
  # --deep не используется (так советует Apple): каждый файл подписывается явно.
  # Сначала все файлы рядом с программой (нативные библиотеки), затем программа и пакет.
  find "$app/Contents/MacOS" -type f ! -name OgnelissNumTouch -print0 |
    xargs -0 -I{} codesign --force --timestamp --options runtime --sign "$MACOS_SIGN_IDENTITY" "{}"
  codesign --force --timestamp --options runtime --entitlements "$ent" --sign "$MACOS_SIGN_IDENTITY" "$app/Contents/MacOS/OgnelissNumTouch"
  codesign --force --timestamp --options runtime --entitlements "$ent" --sign "$MACOS_SIGN_IDENTITY" "$app"
  codesign --verify --strict --verbose=2 "$app"
  echo "Подписано: $MACOS_SIGN_IDENTITY"
else
  codesign --force --deep --sign - "$app" || echo "codesign недоступен — пакет не подписан"
  echo "Подпись ad-hoc (MACOS_SIGN_IDENTITY не задана)"
fi
(cd "$root/artifacts" && rm -f "OgnelissNumTouch-$rid.zip" && ditto -c -k --keepParent "OgnelissNumTouch-$rid.app" "OgnelissNumTouch-$rid.zip")
echo "$app"
