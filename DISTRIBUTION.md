# Распространение Ogneliss NumTouch

Инструкция для автора (частное лицо, индивидуальный разработчик). Что нужно сделать для каждой платформы, чтобы пользователи запускали программу без предупреждений системы. Всё, что можно автоматизировать, уже есть в CI (`.github/workflows/build.yml`). Шаги подписи включаются сами, когда в репозитории появляются нужные секреты, и работают только для ветки `main`. Без секретов сборка идёт как раньше, без подписи.

| Платформа | Что получает пользователь | Что нужно от вас | Стоимость |
|---|---|---|---|
| **macOS** | `.app` открывается без предупреждений, разрешение «Универсальный доступ» сохраняется между обновлениями | Сертификат Developer ID + нотаризация (есть в вашей учётной записи Apple Developer) | входит в Apple Developer Program |
| **Windows — Microsoft Store** | установка и обновления из Store, без SmartScreen | бесплатная регистрация разработчика, отправка пакета MSIX | бесплатно |
| **Windows — SignPath Foundation** | `.exe` с подписью от имени SignPath Foundation (OV); SmartScreen доверяет по мере роста репутации | открытый репозиторий, заявка в SignPath Foundation, шаг подписи в CI | бесплатно для открытых проектов |
| **Windows — свой сертификат** | `.exe` с подписью; SmartScreen предупреждает, пока у подписи нет репутации | OV-сертификат на физлицо (ключ на токене или в облачном HSM) | ≈ $150–300 в год |
| **Windows — без подписи** | `.exe`; при первом запуске SmartScreen: «Подробнее → Выполнить в любом случае» | ничего | бесплатно |
| **Linux** | `tar.gz` + `install.sh`, контрольные суммы | ничего обязательного | бесплатно |

> **Открытый код.** Программа распространяется под лицензией MIT (файл `LICENSE`). Чтобы ссылки на сайте и в программе работали для всех, а для Windows стала доступна бесплатная подпись SignPath Foundation, сделайте репозиторий публичным.

> **Статус проверки.** Скрипты подписи и упаковки написаны и встроены в CI. Без секретов в CI проверено: сборка `.app` с ad-hoc подписью, состав пакета (только файлы Mach-O) и сборка `.msix` через `makeappx`. Сама подпись Developer ID, нотаризация, Azure Artifact Signing и прохождение сертификации Store **не запускались**: для этого нужны ваши учётные данные. Первый запуск каждого пути стоит проверить по чек-листу в конце раздела.

Секреты и переменные задаются в GitHub: **Settings → Secrets and variables → Actions**. Вкладка **Secrets** — для паролей и ключей, вкладка **Variables** — для открытых значений.

---

## macOS: Developer ID и нотаризация

Нотаризация — автоматическая проверка Apple на вредоносный код, а не ревью. Приватный MultitouchSupport ей не мешает: вне Mac App Store его использовать можно. В Mac App Store программа попасть не может: там запрещены приватные API, а песочница не даёт доступа к тачпаду и глобальному вводу.

### 1. Сертификат Developer ID Application (один раз)

Создавать сертификаты Developer ID может только владелец учётной записи (Account Holder). В индивидуальной учётной записи это вы.

1. На Mac откройте **«Связка ключей» → меню «Связка ключей» → «Ассистент сертификации» → «Запросить сертификат у бюро сертификации»**. Укажите e-mail и выберите «Сохранён на диске». Получится файл `CertificateSigningRequest.certSigningRequest`.
2. Откройте [developer.apple.com/account/resources/certificates](https://developer.apple.com/account/resources/certificates/list). Нажмите **«+» → Developer ID Application**, выберите **G2 Sub-CA** и загрузите файл запроса. Скачайте `.cer` и откройте его двойным щелчком: сертификат попадёт в связку «Вход».
   *Альтернатива:* Xcode → Settings → Accounts → Manage Certificates → «+» → Developer ID Application.
3. Проверьте, что ключ на месте, и узнайте точное имя подписи:
   ```bash
   security find-identity -v -p codesigning
   # 1) ABCD… "Developer ID Application: Ivan Petrov (TEAMID1234)"
   ```
4. В «Связке ключей» → «Мои сертификаты» щёлкните правой кнопкой по сертификату и выберите **«Экспортировать»** в формате `.p12`. Задайте пароль. Сертификат экспортируется вместе с закрытым ключом.
5. Подготовьте файл для секрета:
   ```bash
   base64 -i DeveloperID.p12 | pbcopy   # содержимое буфера — значение секрета MACOS_CERT_P12
   ```

### 2. Доступ к службе нотаризации (один раз)

Выберите один способ.

- **Apple ID + пароль приложения (проще).** На [account.apple.com](https://account.apple.com) откройте **«Вход и безопасность» → «Пароли приложений»** и создайте пароль, например «notarytool». Team ID указан на [developer.apple.com/account](https://developer.apple.com/account) → Membership details.
- **Ключ App Store Connect API.** В App Store Connect откройте **Users and Access → Integrations → App Store Connect API → Team Keys**, создайте ключ с ролью Developer и скачайте `.p8`. Скачать его можно только один раз. Запишите Key ID и Issuer ID.

### 3. Секреты в GitHub

| Секрет | Значение |
|---|---|
| `MACOS_CERT_P12` | base64 файла `.p12` (шаг 1.5) |
| `MACOS_CERT_PASSWORD` | пароль экспорта `.p12` |
| `MACOS_SIGN_IDENTITY` | `Developer ID Application: Имя Фамилия (TEAMID)`, точно как в выводе `security find-identity` |
| `AC_APPLE_ID`, `AC_TEAM_ID`, `AC_PASSWORD` | способ «Apple ID»: e-mail Apple ID, Team ID, пароль приложения |
| *или* `AC_API_KEY_P8`, `AC_API_KEY_ID`, `AC_API_ISSUER_ID` | способ «ключ API»: содержимое файла `.p8` (текстом), Key ID, Issuer ID |

### 4. Что делает CI

Для `osx-arm64` и `osx-x64`, при запуске из ветки `main`:

1. Импортирует сертификат во временную связку ключей.
2. Подписывает `packaging/macos/make-app.sh`: каждую нативную библиотеку, затем программу и пакет, с hardened runtime, меткой времени и правами из `packaging/macos/entitlements.plist` (стандартный набор .NET: JIT, исполняемая память, загрузка библиотек).
3. Нотаризует `packaging/macos/notarize.sh`: `notarytool submit --wait`, затем `stapler staple`. После этого пересобирает zip, чтобы Gatekeeper проверял пакет без сети.

Пакет устроен так, чтобы его можно было подписать. Управляемый код упакован в один исполняемый файл, нативные библиотеки (Avalonia, Skia, HarfBuzz) лежат рядом в `Contents/MacOS`. В пакете только файлы Mach-O — это требование нотаризации.

### 5. Локально (без CI)

```bash
dotnet publish src/Ogneliss.NumTouch.App/Ogneliss.NumTouch.App.csproj -p:PublishProfile=osx-arm64
MACOS_SIGN_IDENTITY="Developer ID Application: Имя Фамилия (TEAMID)" bash packaging/macos/make-app.sh osx-arm64
AC_APPLE_ID=you@example.com AC_TEAM_ID=TEAMID AC_PASSWORD=xxxx-xxxx-xxxx-xxxx bash packaging/macos/notarize.sh osx-arm64
```

### Чек-лист первого запуска

- `spctl --assess --type execute --verbose OgnelissNumTouch-osx-arm64.app` → `accepted, source=Notarized Developer ID`.
- Если нотаризация отклонена: `xcrun notarytool log <submission-id> …` покажет, какой файл не прошёл.
- На чистом Mac приложение из zip открывается двойным щелчком. После выдачи «Универсального доступа» и обновления на новую версию разрешение сохраняется, потому что подпись та же. При ad-hoc подписи разрешение приходилось выдавать заново после каждого обновления.

---

## Windows

### Почему не Azure Artifact Signing через Entra ID

Публичные сертификаты сервиса Artifact Signing (Public Trust, им доверяют Windows и SmartScreen) выдаются частным лицам **только из США и Канады**, организациям — из ограниченного списка стран. Учётная запись Entra ID это ограничение не снимает. Профили Private Trust доступны без географических ограничений, но им доверяют только устройства, которые вы настроили сами, например через App Control в своей организации. Для публичного распространения они не подходят.

Шаги CI для Artifact Signing всё равно оставлены. Если правила изменятся или появится организация в подходящей стране, подпись включается заданием переменных (см. «Вариант Г»).

### Вариант А (рекомендуется): Microsoft Store, пакет MSIX

С сентября 2025 года регистрация частного разработчика в Store бесплатна. Нужна личная учётная запись Microsoft и проверка личности: документ и селфи. Пакет подписывает сам Store, свой сертификат не нужен. Store доступен почти в 200 странах; поддерживается ли ваша страна, станет видно при регистрации.

**Регистрация и резервирование имени (один раз):**

1. Зарегистрируйтесь на [storedeveloper.microsoft.com](https://storedeveloper.microsoft.com) как Individual developer.
2. В Партнёрском центре откройте **Apps and games → New product → MSIX or PWA app** и зарезервируйте имя **Ogneliss NumTouch**.
3. Откройте **Product management → Product identity** и скопируйте три значения:
   - `Package/Identity/Name`, например `12345Ogneliss.OgnelissNumTouch`;
   - `Package/Identity/Publisher`, например `CN=XXXXXXXX-XXXX-…`;
   - `Package/Properties/PublisherDisplayName`.
4. В GitHub → **Variables** создайте переменные `MSIX_IDENTITY_NAME`, `MSIX_PUBLISHER`, `MSIX_PUBLISHER_DISPLAY_NAME` с этими значениями.

**Сборка пакета.** CI на каждой сборке собирает `OgnelissNumTouch-win-x64.msix` и `-win-arm64.msix` (артефакт `store-msix`). Пакеты собираются скриптом `packaging/windows/make-msix.ps1` по шаблону `packaging/windows/AppxManifest.xml`. Пока переменные не заданы, в пакете стоит временное удостоверение, и Store такой пакет не примет. Локально:

```powershell
dotnet publish src/Ogneliss.NumTouch.App/Ogneliss.NumTouch.App.csproj -p:PublishProfile=win-x64
pwsh packaging/windows/make-msix.ps1 -Rid win-x64 -IdentityName "…" -Publisher "CN=…" -PublisherDisplayName "…"
```

**Отправка (для каждой версии):**

1. Увеличьте `<Version>` в `src/Ogneliss.NumTouch.App/Ogneliss.NumTouch.App.csproj`. Версия пакета MSIX должна расти с каждой отправкой.
2. Скачайте артефакт `store-msix` из запуска CI на `main`.
3. В Партнёрском центре откройте **Start submission** и заполните разделы:
   - **Packages:** загрузите оба файла `.msix` (x64 и arm64).
   - **Pricing and availability:** Free, выберите рынки.
   - **Properties:** категория *Utilities & tools*. Нужна политика конфиденциальности, ниже есть готовый текст.
   - **Age ratings:** анкета IARC. В программе нет пользовательского контента и сетевых функций.
   - **Store listings:** английский и русский. Готовые тексты ниже, плюс 1–4 скриншота окна программы.
   - **Submission options → Restricted capabilities:** обоснование `runFullTrust`, текст ниже.
4. Отправьте пакет. Сертификация обычно занимает до нескольких рабочих дней.

**Особенности версии из Store** (учтены в коде и манифесте):
- **Автозапуск.** Запись в реестр `Run` из пакета MSIX не действует. В манифесте объявлена задача автозапуска (StartupTask, выключена по умолчанию), пользователь включает её в «Параметры → Приложения → Автозагрузка». В версии из Store флажок автозапуска в настройках программы неактивен, под ним показана эта подсказка.
- **Настройки.** Windows прозрачно перенаправляет запись в `%AppData%` внутрь пакета. Перенос настроек старой версии работает.
- **Обновления** приходят из Store. Обычный `.exe` в Releases можно публиковать параллельно.

**Проверка пакета у себя до отправки.** Неподписанный `.msix` нельзя установить двойным щелчком. Включите «Режим разработчика» (Параметры → Система → Для разработчиков) и зарегистрируйте папку раскладки, которую оставляет скрипт:

```powershell
Add-AppxPackage -Register .\artifacts\msix-win-x64\AppxManifest.xml
```

Проверьте запуск из меню «Пуск», задачу в «Автозагрузке», переключение режима и сохранение настроек. Удаляется пакет через «Параметры → Приложения».

**Обоснование runFullTrust** (поле в Submission options):

> Ogneliss NumTouch is a classic Win32 desktop app (.NET) that turns a Precision Touchpad into a numeric keypad. It needs full trust to read touchpad contacts via Raw Input (HID Digitizer usage page), to send keystrokes with SendInput, to register a global hotkey (RegisterHotKey) and to temporarily block cursor movement with a low-level mouse hook while NumPad mode is on. The app works offline and does not collect or transmit any data.

**Политика конфиденциальности.** Store нужен публичный URL. Репозиторий приватный, поэтому разместите текст, например, на GitHub Pages или в публичном Gist:

> Ogneliss NumTouch works entirely offline. It does not collect, store or transmit personal data. Touch coordinates and pressed keys are processed in memory only and are never recorded; the optional technical log stored on your computer contains only technical events (start, errors, device connection) and can be turned off in Settings.

**Описание для Store.**

*English.* **Ogneliss NumTouch** turns your laptop touchpad into a numeric keypad. Put a sticker with keys on the touchpad, press Ctrl+Alt+N, and touches become real keypresses: digits, Enter, navigation keys, shortcuts or any text. Three ready-made layouts (numpad with trackpad zone, full numpad, numpad with action keys), a visual layout editor, calibration for the exact sticker position, and an on-screen notification when the mode changes. Requires a Precision Touchpad. Works offline.

*Русский.* **Ogneliss NumTouch** превращает тачпад ноутбука в цифровой блок. Наклейте наклейку с клавишами, нажмите Ctrl+Alt+N — и касания становятся настоящими нажатиями: цифры, Enter, навигационные клавиши, сочетания или любой текст. Три готовые раскладки (цифровой блок с зоной трекпада, полный блок, блок с клавишами действий), наглядный редактор раскладок, калибровка точного положения наклейки и экранное уведомление при смене режима. Нужен Precision Touchpad. Работает офлайн.

### Вариант А2: SignPath Foundation — бесплатная подпись для открытых проектов

Программа распространяется под лицензией MIT, поэтому подходит под программу [SignPath Foundation](https://signpath.org/), которая бесплатно подписывает открытые проекты своим сертификатом. В подписи будет указан издатель «SignPath Foundation», а не ваше имя. Условия ([signpath.org/terms](https://signpath.org/terms)), кратко:

- лицензия из списка OSI без коммерческой двойной лицензии — **MIT подходит**; в проекте нет закрытых компонентов;
- подписываются только сборки, сделанные CI из исходников репозитория: наши GitHub Actions подходят;
- сопровождающие владеют репозиторием и используют двухфакторную аутентификацию; каждый релиз подтверждается вручную;
- проект уже выпущен и описан на странице загрузки (страница `ogneliss.ru/numtouch/` и раздел Releases);
- на сайте нужна страница «Политика подписи кода» (code signing policy) — её можно добавить на страницу NumTouch;
- решение о приёме проекта SignPath Foundation принимает на своё усмотрение.

Порядок действий:
1. Сделайте репозиторий публичным (GitHub → Settings → General → Danger Zone → Change visibility) и выпустите хотя бы один релиз.
2. Подайте заявку на signpath.org (кнопка Apply): название, ссылка на репозиторий и сайт.
3. После одобрения SignPath выдаст идентификатор организации, проект и политику подписи. Подпись подключается их действием GitHub Actions: сборка выкладывается артефактом, действие отправляет его на подпись и возвращает подписанные файлы. Этот шаг добавляется в `build.yml` после «Publish» по инструкции SignPath для вашего проекта.

### Вариант Б: свой сертификат подписи кода (OV) на физлицо

Подходит для `.exe` в Releases.

1. Выберите удостоверяющий центр, который выдаёт OV-сертификаты частным лицам и работает с вашей страной. Список стран смотрите на сайте УЦ до оплаты. Проверка личности занимает от 1 до нескольких рабочих дней.
2. С 2023 года закрытый ключ хранится только на аппаратном токене или в облачном HSM УЦ. Файл `.pfx` получить нельзя.
3. Подпись с токеном на своём ПК (Windows SDK):
   ```powershell
   signtool sign /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 /n "Имя Фамилия" artifacts\win-x64\OgnelissNumTouch.exe
   signtool verify /pa /v artifacts\win-x64\OgnelissNumTouch.exe
   ```
   Для облачного HSM у каждого УЦ своя утилита для CI. Её шаг добавляется в `build.yml` вместо шагов Artifact Signing, после «Publish» и до «Package».
4. SmartScreen доверяет не сертификату, а репутации. Первые версии могут показывать предупреждение, пока не наберётся статистика загрузок. Подписывайте все версии одним сертификатом, тогда репутация накапливается. EV-сертификат с 2024 года больше не даёт мгновенного доверия.

### Вариант В: без подписи

Это текущее состояние. В README описан обход SmartScreen («Подробнее → Выполнить в любом случае»). В релиз CI кладёт `SHA256SUMS.txt`: пользователь может сверить файл командой `Get-FileHash OgnelissNumTouch-win-x64.exe`.

### Вариант Г: Azure Artifact Signing (если станет доступен)

Шаги CI уже есть: «Azure login (OIDC)», «Sign Windows executables», «Verify Windows signature». Они включаются переменной `AZURE_SIGNING_ACCOUNT`.

1. **Подписка Azure** в клиенте Entra ID. Зарегистрируйте поставщика ресурсов: Подписка → Resource providers → `Microsoft.CodeSigning` → Register.
2. **Учётная запись Artifact Signing:** Create → регион (например West Europe, конечная точка `https://weu.codesigning.azure.net`) → тариф Basic.
3. **Проверка личности:** роль *Artifact Signing Identity Verifier* для себя, затем Identity validations → New → Public. Занимает от 1 до 20 рабочих дней.
4. **Профиль сертификата:** Certificate profiles → Create → Public Trust → выбрать пройденную проверку.
5. **Приложение для GitHub:** Entra ID → App registrations → New. Затем Certificates & secrets → Federated credentials → GitHub Actions: организация `oedante`, репозиторий, Entity type *Branch*, `main`.
6. **Права:** на профиле сертификата (или учётной записи) назначьте приложению роль **Artifact Signing Certificate Profile Signer**.
7. **GitHub:**
   - Secrets: `AZURE_CLIENT_ID` (Application ID), `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID`.
   - Variables: `AZURE_SIGNING_ENDPOINT` (например `https://weu.codesigning.azure.net/`), `AZURE_SIGNING_ACCOUNT`, `AZURE_SIGNING_PROFILE`.

CI подписывает `OgnelissNumTouch.exe` для x64 и arm64 с меткой времени и проверяет подпись (`Get-AuthenticodeSignature`). Подпись выполняется на x64-раннере для обеих архитектур, раннеры ARM сервис не поддерживает.

---

## Linux

Обязательной подписи нет. Распространение — архив `OgnelissNumTouch-linux-<arch>.tar.gz`: программа, `install.sh`, правило udev, ярлык.

- **Контрольные суммы.** В каждом релизе есть `SHA256SUMS.txt`, проверка: `sha256sum -c SHA256SUMS.txt --ignore-missing`. При желании подпишите файл сумм своим ключом GPG (`gpg --detach-sign --armor SHA256SUMS.txt`) и опубликуйте отпечаток ключа в README.
- **Flatpak и Snap не подходят.** Песочница не даёт доступа к `/dev/input` и `/dev/uinput`. Даже с `--device=all` правило udev и группу `input` придётся настраивать на хосте отдельно, то есть преимущества пакета теряются.
- **.deb / .rpm / AppImage** возможны позже. Пакет `.deb` может сам ставить правило udev в `/usr/lib/udev/rules.d` и не требовать `sudo` из скрипта. Добавлять пользователя в группу `input` всё равно придётся явно, с перелогином.

---

## Выпуск версии (чек-лист)

1. Обновите `<Version>` в `src/Ogneliss.NumTouch.App/Ogneliss.NumTouch.App.csproj` и запись в `CHANGELOG.md`.
2. Слейте изменения в `main` и дождитесь зелёного CI. На `main` подписи и нотаризация выполняются, если секреты заданы.
3. Создайте релиз: GitHub → Actions → build → **Run workflow** (ветка `main`), поле `release_tag`, например `v3.1.0`. В релиз попадут `.exe`, `.tar.gz`, `.zip` и `SHA256SUMS.txt`.
4. Для Store: скачайте артефакт `store-msix` того же запуска и отправьте его в Партнёрском центре (Вариант А).
