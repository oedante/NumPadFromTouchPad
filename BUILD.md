# BUILD.md — сборка, запуск, тесты, релиз

## Требования

| Что | Версия |
|---|---|
| ОС для сборки приложения | Windows 10/11 (WPF собирается только под Windows) |
| .NET SDK | 8.0.x (`dotnet --version` → 8.0.*) |
| IDE (необязательно) | Visual Studio 2022 17.8+ с нагрузкой «Разработка классических приложений .NET» |
| Тесты ядра | любая ОС с .NET 8 SDK (Windows, Linux, macOS) |

Пользователю для запуска релизной сборки ничего устанавливать не нужно (self-contained).

## Команды

```powershell
# из корня репозитория
dotnet restore NumPadFromTouchPad.sln
dotnet build NumPadFromTouchPad.sln -c Release

# тесты общей логики (40+ тестов, включая 20 сценариев ТЗ)
dotnet test tests/NumPad.Core.Tests/NumPad.Core.Tests.csproj -c Release

# запуск из исходников
dotnet run --project src/NumPad.App -c Release
# запуск свёрнутым в трей
dotnet run --project src/NumPad.App -c Release -- --minimized
```

## Релиз (один exe без установки .NET)

```powershell
dotnet publish src/NumPad.App/NumPad.App.csproj -p:PublishProfile=win-x64    # → artifacts/win-x64/NumPadFromTouchPad.exe
dotnet publish src/NumPad.App/NumPad.App.csproj -p:PublishProfile=win-arm64  # → artifacts/win-arm64/NumPadFromTouchPad.exe
```

Профили: `src/NumPad.App/Properties/PublishProfiles/*.pubxml` — self-contained, single-file, ReadyToRun, без обрезки (WPF не поддерживает trimming).

Подпись exe (рекомендуется для распространения, иначе SmartScreen предупреждает):
```powershell
signtool sign /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 /f cert.pfx /p <пароль> artifacts\win-x64\NumPadFromTouchPad.exe
```

## Установка и удаление (для пользователя)

Установка: скопировать `NumPadFromTouchPad.exe` в любую папку (например, `%LocalAppData%\Programs\NumPadFromTouchPad`) и запустить. Права администратора не нужны.

Удаление:
1. В программе: «Настройки» → снять «Запускать вместе с Windows» → «Сохранить» (или удалить значение `NumPadFromTouchPad` в `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`).
2. Выйти через меню значка в трее.
3. Удалить exe и папку настроек `%AppData%\NumPadFromTouchPad`.

## Где лежат данные

`%AppData%\NumPadFromTouchPad\`
- `settings.json` — настройки, профили устройств и калибровки;
- `layouts\*.json` — пользовательские раскладки (файл с Id встроенной раскладки переопределяет её);
- `logs\numpad.log` — технический журнал (ротация 1 МБ × 3);
- `session.lock`, `pending-restore.json` — служебные файлы восстановления после сбоя.

## CI

`.github/workflows/build.yml`: тесты ядра на Linux; сборка, тесты, публикация x64/ARM64 и смоук-запуск (процесс стартует и живёт 5 с) на `windows-latest`. Артефакты: `NumPadFromTouchPad-win-x64`, `NumPadFromTouchPad-win-arm64`.

## macOS

См. `mac/README.md`. Прототип не собирался; команда сборки на Mac: `cd mac && swift build -c release`.
