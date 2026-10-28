<#
.SYNOPSIS
  Собирает клиент ClubOS в папку dist\ (служба + оверлей) для установки на клубные ПК.
.DESCRIPTION
  Сборка «самодостаточная» (self-contained): на клубном ПК не нужно ставить .NET.
  Запускать на машине разработчика, где установлен .NET SDK:  .\deploy\publish.ps1
  Версия берётся из -Version или из client\Directory.Build.props и записывается в dist\version.txt и в сами программы.
  С -Zip ещё получается пакет обновления dist\ClubOS-<версия>.zip (Service, Overlay, version.txt): его подписывают
  (dotnet run --project client\ClubOS.Release -- sign ...) и загружают на сервер, после чего ПК клубов обновляются сами.
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64',
    [string]$Version,                # например 1.2.0; не указана: из Directory.Build.props
    [switch]$Zip                     # собрать ещё и пакет обновления
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $root 'dist'

if (-not $Version) { $Version = ([xml](Get-Content (Join-Path $root 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version }
if ($Version -notmatch '^\d{1,4}\.\d{1,4}\.\d{1,4}(\.\d{1,4})?$') { throw "Версия должна быть вида 1.2.0, а не '$Version'" }
Write-Host "Версия клиента: $Version" -ForegroundColor Cyan
if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
# нативная часть (C++): собираем, если на машине есть компилятор; без неё служба тоже работает (считает хеши на C#)
$nativeBuild = Join-Path $root 'ClubOS.Native\build.ps1'
try {
    Write-Host 'Сборка нативной части (C++)...' -ForegroundColor Cyan
    & $nativeBuild -Configuration $Configuration
} catch {
    Write-Warning "Нативная часть не собрана ($($_.Exception.Message)). Клиент будет без неё: хеши блоков считаются на C#, медленнее."
}
foreach ($p in @(@{ Name = 'ClubOS.Service'; Out = 'Service' }, @{ Name = 'ClubOS.Overlay'; Out = 'Overlay' })) {
    Write-Host "Публикация $($p.Name)..." -ForegroundColor Cyan
    dotnet publish (Join-Path $root $p.Name) -c $Configuration -r $Runtime --self-contained true `
        -p:PublishSingleFile=false -p:Version=$Version -o (Join-Path $dist $p.Out) --nologo
    if ($LASTEXITCODE -ne 0) { throw "Не удалось собрать $($p.Name)" }
}
[IO.File]::WriteAllText((Join-Path $dist 'version.txt'), $Version, (New-Object Text.UTF8Encoding($false)))
Copy-Item (Join-Path $PSScriptRoot 'install.ps1') $dist
Copy-Item (Join-Path $PSScriptRoot 'harden-guest.ps1') $dist
Copy-Item (Join-Path $PSScriptRoot 'uninstall.ps1') $dist
if ($Zip) {
    # в пакет обновления идут только программа и отметка версии; скрипты установки на ПК обновлением не меняются
    $stage = Join-Path ([IO.Path]::GetTempPath()) ("clubos-pkg-" + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory $stage | Out-Null
    foreach ($item in 'Service', 'Overlay', 'version.txt') { Copy-Item (Join-Path $dist $item) $stage -Recurse }
    $zipPath = Join-Path $dist "ClubOS-$Version.zip"
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory($stage, $zipPath, [IO.Compression.CompressionLevel]::Optimal, $false)
    Remove-Item $stage -Recurse -Force
    Write-Host ("Пакет обновления: $zipPath ({0:N1} МБ)" -f ((Get-Item $zipPath).Length / 1MB)) -ForegroundColor Green
    Write-Host "Дальше: dotnet run --project client\ClubOS.Release -- sign --package `"$zipPath`" --key <закрытый ключ>"
}
Write-Host "Готово: $dist" -ForegroundColor Green
Write-Host "Скопируйте содержимое dist на клубный ПК и запустите install.ps1 от имени администратора."
