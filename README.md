<img src="docs/icon.png" width="96" align="right" alt="Ogneliss NumTouch icon">

# Ogneliss NumTouch

**English** · [Русский](README.ru.md)

Turns a laptop touchpad into a numeric keypad. You put a sticker with keys on the touchpad, press a hotkey, and touches on the sticker become real keypresses — `7`, `Enter`, `Delete`, `Print Screen`, arrows, shortcuts such as `Ctrl+C`, or any text.

The program does not draw a keyboard on the screen: it reads real touches from the physical touchpad and sends system keyboard events.

> Formerly *NumPad from TouchPad*. Settings and layouts of the old version are transferred automatically on first start.

## Platforms

| OS | Status | Requirements |
|---|---|---|
| **Windows 10 1809+ / 11** (x64, ARM64) | primary; builds and passes the UI self-test in CI; confirmed working by the author with an Apple touchpad (Precision Touchpad); the full hardware checklist is pending | Precision Touchpad (almost every recent laptop) |
| **Linux** (x64, ARM64; X11 and Wayland) | builds and passes the UI self-test in CI, not yet tested on a real touchpad | any multitouch touchpad; `input` group and a udev rule (installed by `install.sh`) |
| **macOS 12+** (Apple Silicon, Intel) | prototype: builds and starts in CI, not tested on a MacBook | Accessibility permission |

## Installation

Download the file for your system from [Releases](../../releases).

- **Windows:** `OgnelissNumTouch-win-x64.exe` (or `-win-arm64`). No installer and no admin rights needed. If the build is not signed, Windows SmartScreen shows “Windows protected your PC” on first start: click **More info → Run anyway**.
- **Linux:** unpack `OgnelissNumTouch-linux-x64.tar.gz` and run `./install.sh`. It installs the program to `~/.local/bin`, adds a menu entry and, once via `sudo`, adds you to the `input` group and installs a udev rule for `/dev/uinput`. Log out and back in afterwards.
- **macOS:** unpack `OgnelissNumTouch-osx-arm64.zip` (Apple Silicon) or `-osx-x64.zip` (Intel) and move the app to Applications. Grant **Accessibility** permission (System Settings → Privacy & Security) and restart the app. An unsigned build has to be opened the first time via right-click → **Open**.

## Quick start

1. Start the program; its icon appears in the tray (menu bar on macOS).
2. On the **Home** page pick a layout: **NUM10** (numpad on the right, regular trackpad on the left), **NUM20** (full block with navigation and arrows) or **NUM30** (action keys instead of arrows).
3. Adjust the layout on the **Layouts** page if needed: drag keys with the mouse, resize by the corner, set the label and action of each key.
4. Apply the sticker and run **Calibration**: touch the five highlighted keys, check the result and save.
5. Press **Ctrl+Alt+N**: the touchpad works as a numpad, and a short on-screen notification confirms the mode. Press again to get the regular touchpad back.

## Features

- **Layout editor:** move and resize keys by any edge or corner or numerically, with smart snapping to neighboring keys, the layout gap, the edges, equal sizes and the grid (guides, rulers and a live size readout, also in millimetres); gaps, sticker aspect ratio; actions — a key (numpad, navigation, F1–F24, letters, modifiers, media), a shortcut, typing text, or an empty zone; labels; disabled zones; auto-repeat; undo/redo; import/export; restore defaults; error checking; live touchpad contacts drawn over the diagram before saving.
- **Calibration:** five reference keys give the position, scale and tilt of the sticker; dry-run check; manual bounds; a separate profile for each touchpad.
- Hold, auto-repeat, several fingers at once, Shift/Ctrl on the touchpad.
- Real numpad keys (`Num 7`, `Num Enter`…), not the digits of the main row.
- In NumPad mode the cursor does not jump while a finger is on the keys (on Linux the touchpad is grabbed entirely, while the NUM10 trackpad zone keeps moving the cursor).
- On-screen notification when the mode changes (can be turned off in Settings).
- Looks native on every OS: system font, light and dark theme and accent colour from the system settings; on macOS — the app menu in the menu bar, ⌘ shortcuts and macOS button order.
- English and Russian interface; the language follows the system or is chosen in Settings.
- The mode turns off on lock and sleep (Windows); no key is ever left “stuck”.

## Limitations

- Windows: older touchpads without Precision Touchpad do not provide touch coordinates; keypresses do not reach windows running as administrator (UIPI); `Ctrl+Alt+Del` cannot be generated; an external mouse is briefly blocked while the touchpad is touched.
- Linux: the hotkey is not intercepted (the active program receives it too); text input is limited to US-layout characters; `input` group membership is required.
- macOS: uses the private MultitouchSupport API, so the app cannot be in the Mac App Store and may break in future macOS versions; Print Screen takes a full-screen screenshot (⇧⌘3); the Calculator, Mail and Web keys open Calculator and the default mail app and browser; media keys other than volume (such as play/pause) are not available.

## Privacy

Works offline. The log contains technical events only; pressed keys, typed text and touch coordinates are never recorded.

## License

Free and open source under the [MIT license](LICENSE). Contributions are welcome — see [CONTRIBUTING.md](CONTRIBUTING.md). Website: [ogneliss.ru/numtouch](https://ogneliss.ru/numtouch/).

## For developers

[BUILD.md](BUILD.md) — building; [DISTRIBUTION.md](DISTRIBUTION.md) — signing and publishing per platform; [ARCHITECTURE.md](ARCHITECTURE.md) — design; [PLATFORM_FEASIBILITY.md](PLATFORM_FEASIBILITY.md) — platform capabilities; [TESTING.md](TESTING.md) — tests and verification status; [CHANGELOG.md](CHANGELOG.md) — changes. Developer documentation is in Russian.
