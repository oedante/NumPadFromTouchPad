# BUILD.md — сборка, запуск, тесты, релиз

## Требования

| Что | Версия |
|---|---|
| .NET SDK | 10.0.x |
| ОС для сборки | любая: Windows, Linux или macOS (Avalonia собирается везде) |
| IDE (необязательно) | Visual Studio 2022 17.14+/2026, JetBrains Rider, VS Code + C# Dev Kit; расширение Avalonia для предпросмотра XAML |

Пользователю для запуска релизной сборки ничего устанавливать не нужно (self-contained).

## Команды

```bash
dotnet restore OgnelissNumTouch.sln
dotnet build OgnelissNumTouch.sln -c Release
dotnet test tests/Ogneliss.NumTouch.Tests/Ogneliss.NumTouch.Tests.csproj -c Release

dotnet run --project src/Ogneliss.NumTouch.App -c Release                 # запуск
dotnet run --project src/Ogneliss.NumTouch.App -c Release -- --minimized  # сразу в трей
dotnet run --project src/Ogneliss.NumTouch.App -c Release -- --selftest   # самопроверка UI (код выхода 0 — успех)
```

## Релизные сборки

```bash
dotnet publish src/Ogneliss.NumTouch.App/Ogneliss.NumTouch.App.csproj -p:PublishProfile=win-x64     # artifacts/win-x64/OgnelissNumTouch.exe
dotnet publish src/Ogneliss.NumTouch.App/Ogneliss.NumTouch.App.csproj -p:PublishProfile=win-arm64
dotnet publish src/Ogneliss.NumTouch.App/Ogneliss.NumTouch.App.csproj -p:PublishProfile=linux-x64   # artifacts/linux-x64/OgnelissNumTouch
dotnet publish src/Ogneliss.NumTouch.App/Ogneliss.NumTouch.App.csproj -p:PublishProfile=linux-arm64
dotnet publish src/Ogneliss.NumTouch.App/Ogneliss.NumTouch.App.csproj -p:PublishProfile=osx-arm64   # затем packaging/macos/make-app.sh osx-arm64
dotnet publish src/Ogneliss.NumTouch.App/Ogneliss.NumTouch.App.csproj -p:PublishProfile=osx-x64
```

Профили: `src/Ogneliss.NumTouch.App/Properties/PublishProfiles/*.pubxml` — self-contained, single-file, без обрезки.

## Установка

**Windows.** Скопировать `OgnelissNumTouch-win-x64.exe` в любую папку и запустить. Права администратора не нужны. Подпись для распространения: `signtool sign /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 /f cert.pfx OgnelissNumTouch.exe`.

**Linux.** Распаковать `OgnelissNumTouch-linux-x64.tar.gz` и выполнить `./install.sh`: программа ставится в `~/.local/bin`, ярлык — в меню; скрипт один раз через `sudo` добавляет пользователя в группу `input` (доступ к тачпаду и клавиатурам) и ставит правило udev для `/dev/uinput`. После этого нужно выйти из системы и войти снова. На GNOME для значка в трее нужно расширение AppIndicator (в Ubuntu включено по умолчанию).

**macOS.** Распаковать `OgnelissNumTouch-osx-arm64.zip`, перенести `.app` в «Программы». Пакет подписан ad-hoc, поэтому первый запуск — через контекстное меню «Открыть». Затем выдать разрешение «Универсальный доступ» (Системные настройки → Конфиденциальность и безопасность) и перезапустить. Для распространения: подпись Developer ID + `xcrun notarytool submit … --wait` + `xcrun stapler staple`. Приватный MultitouchSupport исключает Mac App Store.

## Удаление

1. «Настройки» → снять «Запускать при входе в систему» → «Сохранить».
2. Выйти через меню трея или кнопку «Выход».
3. Удалить программу и папку настроек: Windows `%AppData%\OgnelissNumTouch`; Linux `~/.config/OgnelissNumTouch`; macOS — путь показан на странице «Диагностика» (кнопка «Открыть журнал» открывает его подпапку). Linux: при желании удалить `/etc/udev/rules.d/70-ogneliss-numtouch.rules`.

## Где лежат данные

`settings.json` (настройки, профили устройств, калибровки), `layouts/*.json` (раскладки пользователя), `logs/numtouch.log` (технический журнал), `session.lock` и `pending-restore.json` (восстановление после сбоя).

## CI

`.github/workflows/build.yml` — матрица Windows / Ubuntu / macOS: сборка решения (предупреждения выводятся аннотациями), тесты, публикация двух архитектур, UI-самопроверка `--selftest` (на Linux — под Xvfb), упаковка (`.exe`, `.tar.gz` с install.sh, `.app.zip`). Ручной запуск с параметром `release_tag` создаёт pre-release со всеми пакетами.
