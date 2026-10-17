#!/usr/bin/env bash
# Нотаризация и «скрепление» (staple) подписанного Developer ID пакета .app. Запуск: notarize.sh osx-arm64
#
# Вход в службу нотаризации — один из двух способов (переменные окружения):
#   1) Apple ID: AC_APPLE_ID, AC_TEAM_ID, AC_PASSWORD (пароль приложения с appleid.apple.com);
#   2) ключ App Store Connect API: AC_API_KEY_PATH (файл .p8), AC_API_KEY_ID, AC_API_ISSUER_ID.
set -euo pipefail
rid="${1:-osx-arm64}"; root="$(cd "$(dirname "$0")/../.." && pwd)"
app="$root/artifacts/OgnelissNumTouch-$rid.app"; zip="$root/artifacts/OgnelissNumTouch-$rid.zip"
if [ -n "${AC_API_KEY_PATH:-}" ]; then
  auth=(--key "$AC_API_KEY_PATH" --key-id "$AC_API_KEY_ID" --issuer "$AC_API_ISSUER_ID")
else
  auth=(--apple-id "$AC_APPLE_ID" --team-id "$AC_TEAM_ID" --password "$AC_PASSWORD")
fi
# Нотаризации отправляется zip; после одобрения билет скрепляется с .app, и zip пересобирается уже с ним,
# чтобы Gatekeeper проверял пакет без обращения к сети.
xcrun notarytool submit "$zip" "${auth[@]}" --wait --timeout 30m
xcrun stapler staple "$app"
xcrun stapler validate "$app"
spctl --assess --type execute --verbose "$app"
(cd "$root/artifacts" && rm -f "OgnelissNumTouch-$rid.zip" && ditto -c -k --keepParent "OgnelissNumTouch-$rid.app" "OgnelissNumTouch-$rid.zip")
echo "Нотаризовано: $zip"
