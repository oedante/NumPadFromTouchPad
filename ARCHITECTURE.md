# ARCHITECTURE.md

## Стек

C# / **.NET 10 (LTS)** / **Avalonia 11** — один код интерфейса для Windows, Linux и macOS. Всё платформенное (касания, вывод клавиш, блокировка курсора, горячая клавиша, автозапуск) — за интерфейсами `NumPad.Platform`; реализация выбирается во время выполнения (`PlatformFactory`).

История: 2.0 — WPF только для Windows (тег `v2.0.0-rc1`); 3.0 — переход на Avalonia, общий платформенный слой, бэкенды Linux и macOS.

## Структура решения

```
NumPadFromTouchPad.sln
├─ src/NumPad.Core              — логика без ОС: автомат нажатий, раскладки, hit-testing, калибровка, настройки, коды клавиш
├─ src/NumPad.Platform          — интерфейсы платформы, монотонные часы, сборка кадров PTP, заглушка NullPlatform
├─ src/NumPad.Platform.Windows  — Raw Input (PTP), SendInput, RegisterHotKey, WH_MOUSE_LL, SPI тачпада, реестр
├─ src/NumPad.Platform.Linux    — evdev (тачпад, клавиатуры), uinput (клавиши, курсор), EVIOCGRAB, XDG autostart
├─ src/NumPad.Platform.MacOS    — MultitouchSupport, CGEvent, Carbon hotkey, CGEventTap, LaunchAgent (прототип)
├─ src/NumPad.App               — Avalonia: окно, 5 страниц, трей, контроллер
├─ tests/NumPad.Core.Tests      — xUnit: ядро + платформенная логика без ввода-вывода (на всех 3 ОС в CI)
└─ packaging/                   — linux: udev-правило, .desktop, install.sh; macos: сборка .app
```

## Интерфейсы платформы (`NumPad.Platform/Abstractions.cs`)

| Интерфейс | Windows | Linux | macOS |
|---|---|---|---|
| `ITouchpadBackend` | Raw Input, HID-коллекция 0x0D/0x05 (только Precision Touchpad) | `/dev/input/eventN`, протокол multitouch B (любой тачпад с драйвером ядра) | `MultitouchSupport.framework` (приватный) |
| `IKeyboardOutput` | `SendInput` VK + скан-код + extended | виртуальная клавиатура `/dev/uinput` | `CGEventPost` + флаг NumericPad |
| `IPointerSuppressor` | `WH_MOUSE_LL` пока палец на клавишах + хвост | `EVIOCGRAB` (захват тачпада) + виртуальная мышь для «зоны трекпада» | `CGEventTap` пока палец на клавишах + хвост |
| `IGlobalHotkey` | `RegisterHotKey` (перехватывается) | чтение клавиатур evdev (не перехватывается) | Carbon `RegisterEventHotKey` |
| `IGestureGuard` | `SPI_SETTOUCHPADPARAMETERS` (Win 11 24H2+) | — (захват и так отключает жесты) | — |
| `IAutostart` | `HKCU\…\Run` | `~/.config/autostart/*.desktop` | `~/Library/LaunchAgents/*.plist` |
| `SystemEvent` | блокировка (WTS), сон (WM_POWERBROADCAST) | — (сторож движка) | — (сторож движка) |

Потоки: Windows — отдельный поток ввода со скрытым окном (Raw Input, хоткей и LL-хук не зависят от загрузки UI); Linux — поток чтения на каждое устройство; macOS — поток MultitouchSupport. Контроллер переводит события в UI-поток Avalonia; движок работает только в UI-потоке. Решение «блокировать указатель» хук читает из `volatile`-полей, которые контроллер обновляет после каждого кадра.

## Поток данных

```
бэкенд (поток ввода) ─► TouchFrame (норм. 0..1) ─► Dispatcher.UIThread ─► ModeManager ─► KeyPressEngine ─► IKeyboardOutput
                                                                    │                     └► ReportTouchState ─► IPointerSuppressor
                                                                    └► FrameArrived ─► страницы (предпросмотр, редактор, калибровка, диагностика)
DispatcherTimer 10 мс ─► Engine.Tick (автоповтор, сторож, повтор KeyUp)
```

## Модель состояний (центральное исправление дефекта повторных нажатий)

1. **Физическое касание** — `TouchContact{Id, X, Y, Tip, Confidence}` в кадре.
2. **Логическое нажатие** — состояние контакта в движке: зона фиксируется в момент касания, живёт до отрыва этого Contact ID (Tip = 0, исчезновение из полного кадра или сторож).
3. **Системное событие** — `KeyDown/KeyUp` через счётчик ссылок на клавишу.

Политики: срабатывание при касании / при отпускании; удержание или отпускание при соскальзывании; модификаторы удерживаются пальцем; ладонь игнорируется; сторож зависших контактов; `ReleaseAll` при выключении режима, смене раскладки, блокировке, сне, потере устройства, выходе, сбое.

## Координаты

Устройство: 0..1 по логическому диапазону (Y вниз). Наклейка: 0..1 области; калибровка — аффинное преобразование (6 параметров, МНК по 5 опорным клавишам) или ручной прямоугольник.

## Сознательно не делаем

Собственный драйвер; MVVM-фреймворк и DI-контейнер (для 5 страниц избыточны); плагины на клавишах.
