# macOS — прототип (не проверен)

**Статус: код не собирался и не запускался** — в среде, где он написан, нет macOS и Xcode. Поддержка macOS не заявляется.

Что есть: `Sources/NumPadMac/main.swift` — консольный прототип, использующий тот же JSON-формат раскладки, что и Windows-версия:

- ввод — приватный `MultitouchSupport.framework` (через `dlopen`), нормализованные координаты пальцев в фоне;
- вывод — `CGEventPost` с keypad-кодами `kVK_ANSI_Keypad*`;
- хоткей — `Ctrl+Option+N` (Carbon `RegisterEventHotKey`);
- подавление курсора — `CGEventTap`, пока палец на клавишах.

Чего нет: калибровки (используется тождественное преобразование), UI, трея, автоповтора, режима «при отпускании», сторожа.

## Проверка на MacBook

```bash
cd mac
swift build -c release
# экспортировать раскладку из Windows-версии (Раскладки → Экспорт…) и положить в
# ~/Library/Application Support/NumPadFromTouchPad/layout.json
.build/release/NumPadMac
```

Разрешения: «Универсальный доступ» (Accessibility) — для `CGEventPost` и `CGEventTap`; возможно «Мониторинг ввода».

Что проверить в первую очередь:
1. `MTDeviceCreateList` возвращает встроенный трекпад на Apple Silicon (macOS 13–15).
2. Раскладка полей `MTTouch` соответствует текущей macOS (неверная раскладка даст «мусорные» координаты).
3. `CGEventTap` блокирует движение курсора и тап-клики.

Распространение: приватный API исключает Mac App Store; возможны Developer ID + нотаризация.
