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

Профили: `src/Ogneliss.NumTouch.App/Properties/PublishProfiles/*.pubxml` — self-contained, single-file, без обрезки. Для macOS нативные библиотеки остаются рядом с программой (их нужно подписывать по отдельности).

Пакет MSIX для Microsoft Store: `pwsh packaging/windows/make-msix.ps1 -Rid win-x64` (после публикации win-x64). Подпись и нотаризация — в [DISTRIBUTION.md](DISTRIBUTION.md).

## Установка

**Windows.** Скопировать `OgnelissNumTouch-win-x64.exe` в любую папку и запустить. Права администратора не нужны. Варианты подписи — [DISTRIBUTION.md](DISTRIBUTION.md).

**Linux.** Распаковать `OgnelissNumTouch-linux-x64.tar.gz` и выполнить `./install.sh`: программа ставится в `~/.local/bin`, ярлык — в меню; скрипт один раз через `sudo` добавляет пользователя в группу `input` (доступ к тачпаду и клавиатурам) и ставит правило udev для `/dev/uinput`. После этого нужно выйти из системы и войти снова. На GNOME для значка в трее нужно расширение AppIndicator (в Ubuntu включено по умолчанию).

**macOS.** Распаковать `OgnelissNumTouch-osx-arm64.zip`, перенести `.app` в «Программы». Сборка без секретов подписана ad-hoc, поэтому первый запуск — через контекстное меню «Открыть»; сборка из `main` с настроенными секретами подписана Developer ID и нотаризована. Затем выдать разрешение «Универсальный доступ» (Системные настройки → Конфиденциальность и безопасность) и перезапустить. Приватный MultitouchSupport исключает Mac App Store.

## Удаление

1. «Настройки» → снять «Запускать при входе в систему» → «Сохранить».
2. Выйти через меню трея или кнопку «Выход».
3. Удалить программу и папку настроек: Windows `%AppData%\OgnelissNumTouch`; Linux `~/.config/OgnelissNumTouch`; macOS — путь показан на странице «Диагностика» (кнопка «Открыть журнал» открывает его подпапку). Linux: при желании удалить `/etc/udev/rules.d/70-ogneliss-numtouch.rules`.

## Где лежат данные

`settings.json` (настройки, профили устройств, калибровки), `layouts/*.json` (раскладки пользователя), `logs/numtouch.log` (технический журнал), `session.lock` и `pending-restore.json` (восстановление после сбоя).

При первом запуске после переименования `settings.json` и `layouts/*.json` копируются из папки прежней версии (`…/NumPadFromTouchPad`); старая папка не изменяется. Записи автозапуска старой версии удаляются и при включённом автозапуске создаются заново.

## Локализация

Ключ перевода — английский текст: `L.T("Save")`, `L.F("Point {0} of {1}", a, b)`, в XAML — `{l:T 'Save'}`. Русский перевод — `src/Ogneliss.NumTouch.Core/Localization/RussianStrings.cs`. Тест `LocalizationTests` находит все ключи в исходниках и не пропустит строку без перевода, перевод без использования, несовпадение `{0}` или русский текст, оставшийся прямо в коде или XAML. Добавить язык: новая таблица + пункт в `UiLanguage` и в списке языков на странице настроек. Журнал пишется по-английски независимо от языка интерфейса.

## CI

`.github/workflows/build.yml` — матрица Windows / Ubuntu / macOS: сборка решения (предупреждения выводятся аннотациями), тесты, публикация двух архитектур, UI-самопроверка `--selftest` (на Linux — под Xvfb), упаковка (`.exe`, `.tar.gz` с install.sh, `.app.zip`, `.msix` для Store — отдельный артефакт `store-msix`). На ветке `main` при заданных секретах — подпись Windows (Azure Artifact Signing), подпись Developer ID и нотаризация macOS (см. [DISTRIBUTION.md](DISTRIBUTION.md)). Самопроверка открывает все страницы на обоих языках и показывает уведомление о смене режима. Ручной запуск с параметром `release_tag` создаёт pre-release со всеми пакетами и `SHA256SUMS.txt`.
