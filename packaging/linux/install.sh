#!/usr/bin/env bash
# Установка NumPad from TouchPad для текущего пользователя (~/.local) + правило udev (нужен sudo один раз).
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
bin="$HOME/.local/bin"; apps="$HOME/.local/share/applications"; icons="$HOME/.local/share/icons/hicolor/128x128/apps"
mkdir -p "$bin" "$apps" "$icons"
install -m 755 "$here/NumPadFromTouchPad" "$bin/numpad-from-touchpad"
install -m 644 "$here/numpad-from-touchpad.desktop" "$apps/"
install -m 644 "$here/numpad-from-touchpad.png" "$icons/"
echo "Программа установлена в $bin/numpad-from-touchpad"

if ! id -nG "$USER" | grep -qw input; then
  echo "Добавляю $USER в группу input (доступ к тачпаду и клавиатурам)…"
  sudo usermod -aG input "$USER"
  relogin=1
fi
if [ ! -f /etc/udev/rules.d/70-numpad-from-touchpad.rules ]; then
  echo "Устанавливаю правило udev для /dev/uinput…"
  sudo install -m 644 "$here/70-numpad-from-touchpad.rules" /etc/udev/rules.d/
  sudo modprobe uinput || true
  echo uinput | sudo tee /etc/modules-load.d/numpad-from-touchpad.conf >/dev/null
  sudo udevadm control --reload-rules && sudo udevadm trigger --name-match=uinput || true
fi
[ "${relogin:-0}" = 1 ] && echo "Выйдите из системы и войдите снова, чтобы права группы input вступили в силу."
echo "Готово."
