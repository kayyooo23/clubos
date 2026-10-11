<#
.SYNOPSIS
  Собирает папку и zip «ClubOS-laptop-test» для проверки клиента на втором компьютере (без установки в систему).
.DESCRIPTION
  Берёт готовую публикацию из dist\ (если её нет — запускает publish.ps1), кладёт рядом файл настроек agent.json
  (адрес сервера и ключ этого ПК), скрипты запуска и инструкцию. Результат: dist\ClubOS-laptop-test и dist\ClubOS-laptop-test.zip
  Файл agent.json создаёт  python -m scripts.add_pc <клуб> <имя ПК> --server http://IP:8000 --write-agent-json <файл>
.EXAMPLE
  .\deploy\make-test-package.ps1 -AgentJson C:\temp\agent.json
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$AgentJson,
    [switch]$Rebuild
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $root 'dist'
$pkg = Join-Path $dist 'ClubOS-laptop-test'
$zip = "$pkg.zip"

if (-not (Test-Path $AgentJson)) { throw "Не найден файл настроек: $AgentJson" }
$cfg = Get-Content $AgentJson -Raw | ConvertFrom-Json
if (-not $cfg.server -or -not $cfg.pc_key) { throw 'В agent.json должны быть поля server и pc_key' }

if ($Rebuild -or -not (Test-Path (Join-Path $dist 'Service\ClubOS.Service.exe')) -or -not (Test-Path (Join-Path $dist 'Overlay\ClubOS.Overlay.exe'))) {
    & (Join-Path $PSScriptRoot 'publish.ps1')
    if ($LASTEXITCODE -ne 0) { throw 'Сборка не удалась' }
}

if (Test-Path $pkg) { Remove-Item $pkg -Recurse -Force }
if (Test-Path $zip) { Remove-Item $zip -Force }
New-Item -ItemType Directory -Force -Path $pkg | Out-Null
Copy-Item (Join-Path $dist 'Service') (Join-Path $pkg 'Service') -Recurse
Copy-Item (Join-Path $dist 'Overlay') (Join-Path $pkg 'Overlay') -Recurse
Copy-Item (Join-Path $PSScriptRoot 'laptop\*') $pkg -Recurse
Copy-Item $AgentJson (Join-Path $pkg 'agent.json')

# инструкция и скрипты с кириллицей — в UTF-8 с BOM, чтобы Блокнот и Windows PowerShell 5.1 читали их правильно
$utf8Bom = New-Object System.Text.UTF8Encoding($true)
foreach ($name in 'README.txt', 'check-connection.ps1') {
    $path = Join-Path $pkg $name
    $text = [IO.File]::ReadAllText($path, [Text.Encoding]::UTF8).Replace('{{SERVER}}', $cfg.server.TrimEnd('/'))
    [IO.File]::WriteAllText($path, $text, $utf8Bom)
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($pkg, $zip, [IO.Compression.CompressionLevel]::Optimal, $true)

Write-Host ''
Write-Host "Готово:" -ForegroundColor Green
Write-Host "  папка: $pkg"
Write-Host ("  zip:   {0}  ({1:N0} МБ)" -f $zip, ((Get-Item $zip).Length / 1MB))
Write-Host "Внутри agent.json лежит КЛЮЧ ЭТОГО ПК: не отправляйте архив посторонним."
