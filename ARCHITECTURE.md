# ARCHITECTURE.md

## Решение по объединению

Основа — **старый проект** (рабочий конвейер Raw Input → PTP). Его P/Invoke-слой перенесён и исправлен, логика нажатий заменена конечным автоматом. Из **нового проекта** взята концепция визуального конструктора (палитра шаблонов, сетка, удаление), но реализована поверх модели данных. Визуальный язык (тёмная тема, жёлтый акцент) сохранён.

Стек не менялся: C# / .NET 8 / WPF (+ WinForms только для `NotifyIcon`). Пакет `InputSimulator` удалён (заменён прямым `SendInput`) — это единственное изменение зависимостей.

## Структура решения

```
NumPadFromTouchPad.sln
├─ src/NumPad.Core          net8.0      — вся платформенно-независимая логика (без Windows API)
├─ src/NumPad.App           net8.0-windows — WPF-приложение, Windows-бэкенды
├─ tests/NumPad.Core.Tests  net8.0      — xUnit, запускается на любой ОС
└─ mac/                     Swift-прототип бэкенда macOS (не проверен)
```

## Компоненты (логическая схема ТЗ → код)

| Компонент ТЗ | Где | Ответственность |
|---|---|---|
| Touchpad Input Backend | `App/Input/PrecisionTouchpad.cs`, `App/Input/HidReportParser.cs` | Raw Input `0x0D/0x05`, разбор HID-отчёта (X, Y, Contact ID, Tip Switch, Confidence, Contact Count), сборка кадров гибридного режима, нормализация к 0..1. Выдаёт `Core.TouchFrame` |
| Input Mode Manager | `Core/Engine/ModeManager.cs` | Обычный режим ↔ NumPad; антидребезг; при выключении — `ReleaseAll` |
| Calibration Engine | `Core/Calibration/*` | Аффинное преобразование «координаты тачпада → координаты наклейки», решение МНК по опорным точкам, оценка погрешности, ручные границы |
| Layout Engine | `Core/Layouts/*` | Модель раскладки (JSON), встроенные NUM10/20/30, валидация |
| Hit Testing Engine | `Core/Layouts/HitTester.cs` | Точка → зона; правило перекрытия — ближайший центр |
| Keyboard Output Backend | `Core/Engine/IKeyboardOutput.cs` (контракт), `App/Output/SendInputKeyboard.cs` | `SendInput` VK+scan+extended, Unicode-текст |
| Hotkey Manager | `Core/Hotkeys/Hotkey.cs` (разбор, предупреждения), `App/Platform/GlobalHotkey.cs` | `RegisterHotKey`, сообщение о конфликте |
| Configuration Manager | `Core/Config/*` | `%AppData%\NumPadFromTouchPad\settings.json`, `layouts\*.json`; атомарная запись; карантин повреждённых файлов |
| Device Manager | `App/Input/PrecisionTouchpad.cs` (перечисление), `Core/Config/DeviceProfile.cs` | Список PTP, профиль калибровки на устройство (ключ VID:PID) |
| Application UI | `App/Views/*` | Главный экран, редактор, калибровка, настройки, диагностика; трей |
| Diagnostics and Logging | `Core/Diagnostics/FileLog.cs` | Только технические события; **нажатые клавиши не пишутся** |
| Pointer suppression | `App/Platform/PointerSuppressor.cs`, `App/Platform/TouchpadGestureGuard.cs` | `WH_MOUSE_LL` на время касания; отключение тапов/жестов через `SPI_SETTOUCHPADPARAMETERS` с гарантированным восстановлением |
| Crash recovery | `Core/Config/SessionGuard.cs` | Маркер сеанса; после сбоя — восстановление настроек тачпада и отпускание модификаторов |

## Поток данных

```
WM_INPUT ─► HidReportParser ─► FrameAssembler ─► TouchFrame (норм. 0..1)
                                                   │
                         ModeManager.IsNumPad? ────┤ нет → только диагностика/калибровка
                                                   ▼
                                  KeyPressEngine.ProcessFrame
                                  ├─ Calibration.Transform (устройство → наклейка)
                                  ├─ HitTester (зона при касании)
                                  ├─ автомат контакта: Down → Held → Up
                                  ├─ счётчик ссылок на клавишу
                                  └─ IKeyboardOutput.KeyDown/KeyUp/TypeText
DispatcherTimer ─► KeyPressEngine.Tick (автоповтор, сторож зависших контактов)
```

## Модель состояний (центральное исправление)

Три уровня, как требует ТЗ:

1. **Физическое касание** — `TouchContact{Id, X, Y, Tip, Confidence}` в кадре.
2. **Логическое нажатие зоны** — `ContactState` в движке: зона фиксируется в момент касания; состояние живёт до отпускания этого Contact ID (Tip=0, исчезновение из полного кадра или сторож).
3. **Системное событие** — `KeyDown`/`KeyUp` через счётчик ссылок `_keyRefCount[key]`: два пальца на одной зоне дают одно нажатие и одно отпускание.

Повторное нажатие не зависит от координат: новое касание = новый (или повторно использованный, но после отпускания) Contact ID → новое логическое нажатие. Таймеров-«разблокировщиков» нет.

Политики (настраиваются):
- `PressTrigger`: `OnTouchDown` (по умолчанию: KeyDown при касании, KeyUp при отпускании — как настоящая клавиша, с удержанием) или `OnRelease` (клавиша срабатывает при отпускании, если палец остался в зоне — защита от случайных касаний).
- `SlideBehavior`: `KeepKey` (по умолчанию) — палец, съехавший в соседнюю зону, продолжает удерживать исходную клавишу; `ReleaseOnExit` — выход из зоны отпускает клавишу.
- Мультитач: каждый Contact ID независим; модификаторы (Shift/Ctrl/Alt/Win) удерживаются, пока на зоне лежит палец, и применяются к клавишам других пальцев.
- `Confidence = 0` (ладонь) — контакт игнорируется.
- Сторож: контакт без обновлений дольше `StaleContactTimeoutMs` (500 мс; PTP шлёт отчёты ≥ 125 Гц, пока есть касание) считается отпущенным — защита от потерянного отчёта об отпускании, а не задержка.

`ReleaseAll` вызывается при: выключении режима, смене раскладки/калибровки, блокировке сеанса, сне, отключении устройства, ошибке, завершении приложения (включая необработанное исключение).

## Координаты

- Устройство: `0..1` по логическому диапазону HID (ось Y вниз).
- Наклейка (раскладка): `0..1` по ширине и высоте области NumPad; `AspectRatio` раскладки — только для отображения.
- Калибровка: `area = A · device + b` (аффинное, 6 параметров) — учитывает смещение, масштаб, небольшой поворот наклейки. По умолчанию — тождественное (наклейка = весь тачпад).

## Решения, от которых сознательно отказались

- Собственный драйвер — см. `PLATFORM_FEASIBILITY.md`.
- MVVM-фреймворк / DI-контейнер — для приложения этого размера избыточны; страницы UI работают с `AppController` напрямую.
- Плагины / скрипты на клавишах — вне задачи.
