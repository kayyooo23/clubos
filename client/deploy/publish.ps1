<#
.SYNOPSIS
  Собирает клиент ClubOS в папку dist\ (служба + оверлей) для установки на клубные ПК.
.DESCRIPTION
  Сборка «самодостаточная» (self-contained): на клубном ПК не нужно ставить .NET.
  Запускать на машине разработчика, где установлен .NET SDK:  .\deploy\publish.ps1
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $root 'dist'

if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
foreach ($p in @(@{ Name = 'ClubOS.Service'; Out = 'Service' }, @{ Name = 'ClubOS.Overlay'; Out = 'Overlay' })) {
    Write-Host "Публикация $($p.Name)..." -ForegroundColor Cyan
    dotnet publish (Join-Path $root $p.Name) -c $Configuration -r $Runtime --self-contained true `
        -p:PublishSingleFile=false -o (Join-Path $dist $p.Out) --nologo
    if ($LASTEXITCODE -ne 0) { throw "Не удалось собрать $($p.Name)" }
}
Copy-Item (Join-Path $PSScriptRoot 'install.ps1') $dist
Copy-Item (Join-Path $PSScriptRoot 'harden-guest.ps1') $dist
Copy-Item (Join-Path $PSScriptRoot 'uninstall.ps1') $dist
Write-Host "Готово: $dist" -ForegroundColor Green
Write-Host "Скопируйте содержимое dist на клубный ПК и запустите install.ps1 от имени администратора."
