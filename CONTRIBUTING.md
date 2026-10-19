# Contributing to Ogneliss NumTouch

**English** · [Русский](#участие-в-разработке)

Thank you for helping! Ogneliss NumTouch is free software under the [MIT license](LICENSE).

- **Bugs and ideas:** open an issue. For touchpad problems, attach the report from *Diagnostics → Copy report* (it contains no typed text or keys).
- **Code:** fork, create a branch, open a pull request. CI builds on Windows, Linux and macOS, runs the tests and a UI self-test; please keep it green.
- **Build:** .NET 10 SDK — see [BUILD.md](BUILD.md). `dotnet test tests/Ogneliss.NumTouch.Tests/Ogneliss.NumTouch.Tests.csproj`.
- **Translations:** English text is the key (`L.T("…")`, `{l:T '…'}` in XAML); Russian is in `src/Ogneliss.NumTouch.Core/Localization/RussianStrings.cs`. The test `LocalizationTests` fails on a missing or unused translation.
- **Look and feel:** the app follows each OS — system font, light/dark theme and accent colour, button order. Platform colours live in `src/Ogneliss.NumTouch.App/Services/PlatformTheme.cs`; use `DynamicResource` for anything theme-dependent.
- **Privacy rule:** never log or store typed text, pressed keys or touch coordinates.

By contributing you agree that your contribution is licensed under the MIT license.

---

## Участие в разработке

Спасибо за помощь! Ogneliss NumTouch — свободная программа под [лицензией MIT](LICENSE).

- **Ошибки и идеи:** создайте issue. Если проблема с тачпадом — приложите отчёт из «Диагностика → Копировать отчёт» (в нём нет вводимого текста и клавиш).
- **Код:** форк, ветка, pull request. CI собирает программу на Windows, Linux и macOS, запускает тесты и самопроверку интерфейса.
- **Сборка:** .NET 10 SDK — см. [BUILD.md](BUILD.md).
- **Перевод:** ключ — английский текст; русский — в `RussianStrings.cs`; тест `LocalizationTests` не пропустит строку без перевода.
- **Внешний вид:** программа выглядит как своя на каждой ОС (системный шрифт, светлая/тёмная тема и акцентный цвет, порядок кнопок); цвета — в `PlatformTheme.cs`, всё, что зависит от темы, — через `DynamicResource`.
- **Правило конфиденциальности:** нельзя записывать вводимый текст, нажатые клавиши и координаты касаний.

Отправляя изменения, вы соглашаетесь распространять их под лицензией MIT.
