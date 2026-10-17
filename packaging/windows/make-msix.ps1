<#
  Сборка пакета MSIX для Microsoft Store из результата dotnet publish (artifacts/<rid>).
  Пакет НЕ подписывается: Store подписывает его сам при публикации. Для установки в обход Store
  (проверка на своём ПК) нужна подпись своим сертификатом или режим разработчика — см. DISTRIBUTION.md.

  Пример (значения из Партнёрского центра → Удостоверение продукта):
    pwsh packaging/windows/make-msix.ps1 -Rid win-x64 `
      -IdentityName "12345Ogneliss.OgnelissNumTouch" -Publisher "CN=XXXXXXXX-XXXX-XXXX-XXXX-XXXXXXXXXXXX" `
      -PublisherDisplayName "Ogneliss"
#>
param(
  [string]$Rid = "win-x64",
  [string]$IdentityName = "Ogneliss.NumTouch",
  [string]$Publisher = "CN=Ogneliss NumTouch (unregistered)",
  [string]$PublisherDisplayName = "Ogneliss",
  [string]$Version = ""
)
$ErrorActionPreference = "Stop"
$root = Resolve-Path "$PSScriptRoot/../.."
if (-not $Version) {
  [xml]$proj = Get-Content "$root/src/Ogneliss.NumTouch.App/Ogneliss.NumTouch.App.csproj"
  $v = ($proj.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
  $Version = ($v -replace '-.*$', '') + ".0"   # MSIX: четыре числа, последнее — 0 (требование Store)
}
$arch = switch ($Rid) { "win-x64" { "x64" } "win-arm64" { "arm64" } default { throw "Неизвестный RID: $Rid" } }

$exe = "$root/artifacts/$Rid/OgnelissNumTouch.exe"
if (-not (Test-Path $exe)) { throw "Нет $exe — сначала dotnet publish -p:PublishProfile=$Rid" }

$layout = "$root/artifacts/msix-$Rid"
Remove-Item -Recurse -Force $layout -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force "$layout/Assets" | Out-Null
Copy-Item $exe $layout
Copy-Item "$PSScriptRoot/Assets/*.png" "$layout/Assets/"
$manifest = (Get-Content "$PSScriptRoot/AppxManifest.xml" -Raw).
  Replace("{{IDENTITY_NAME}}", $IdentityName).
  Replace("{{PUBLISHER}}", [Security.SecurityElement]::Escape($Publisher)).
  Replace("{{PUBLISHER_DISPLAY_NAME}}", [Security.SecurityElement]::Escape($PublisherDisplayName)).
  Replace("{{VERSION}}", $Version).
  Replace("{{ARCH}}", $arch)
Set-Content -Path "$layout/AppxManifest.xml" -Value $manifest -Encoding UTF8

# makeappx.exe из Windows SDK (есть на раннерах GitHub windows-*, локально — установите Windows SDK).
$makeappx = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\makeappx.exe" -ErrorAction SilentlyContinue |
  Sort-Object FullName -Descending | Select-Object -First 1
if (-not $makeappx) { throw "makeappx.exe не найден: установите Windows SDK" }

New-Item -ItemType Directory -Force "$root/dist" | Out-Null
$out = "$root/dist/OgnelissNumTouch-$Rid.msix"
& $makeappx.FullName pack /o /d $layout /p $out
if ($LASTEXITCODE -ne 0) { throw "makeappx завершился с кодом $LASTEXITCODE" }
Write-Host "MSIX: $out (версия $Version, $arch, без подписи — для загрузки в Партнёрский центр)"
