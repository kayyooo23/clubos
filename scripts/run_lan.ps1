<#
.SYNOPSIS
  Запускает сервер ClubOS так, чтобы к нему могли подключаться другие компьютеры домашней/клубной сети (например, ноутбук).
.DESCRIPTION
  • Слушает ТОЛЬКО адрес локальной сети (192.168.x.x / 10.x / 172.16-31.x), а не все адреса подряд: сервер не будет виден
    через VPN-адаптеры (Radmin VPN и т. п.).
  • Ключ платформы на время запуска случайный (стандартный «platform-dev-key» известен всем, кто видел проект).
  • Перед стартом применяет миграции базы.
  • Коды «SMS» при регистрации гостей печатаются в ЭТО окно.
  Остановить: Ctrl+C. Если адрес определился неверно: .\run_lan.ps1 -Bind 192.168.1.80
#>
[CmdletBinding()]
param(
    [string]$Bind = '',
    [int]$Port = 8000
)
$ErrorActionPreference = 'Stop'
Set-Location (Split-Path -Parent $PSScriptRoot)

if (-not $Bind) {
    $found = @(Get-NetIPConfiguration |
        Where-Object { $_.IPv4DefaultGateway -and $_.NetAdapter.Status -eq 'Up' } |
        ForEach-Object { $_.IPv4Address.IPAddress } |
        Where-Object { $_ -match '^(192\.168\.|10\.|172\.(1[6-9]|2[0-9]|3[01])\.)' })
    if ($found.Count -eq 0) { throw 'Не нашёл адрес в локальной сети. Укажите вручную, например: run_lan.cmd -Bind 192.168.1.80' }
    $Bind = $found[0]
    if ($found.Count -gt 1) { Write-Warning ("Найдено несколько адресов: {0}. Используется {1}. Другой можно задать через -Bind." -f ($found -join ', '), $Bind) }
}

if (-not $env:CLUB_PLATFORM_KEY) {
    $bytes = New-Object byte[] 16
    $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
    $rng.GetBytes($bytes)
    $env:CLUB_PLATFORM_KEY = -join ($bytes | ForEach-Object { $_.ToString('x2') })
}

Write-Host 'Применяю миграции базы...' -ForegroundColor DarkGray
python -m alembic upgrade head
if ($LASTEXITCODE -ne 0) { throw 'Не удалось подготовить базу' }

Write-Host ''
Write-Host '  ClubOS сервер запускается' -ForegroundColor Green
Write-Host ('  Админка (с любого ПК сети):  http://{0}:{1}/admin/' -f $Bind, $Port)
Write-Host ('  Адрес для клиента на ПК:     http://{0}:{1}' -f $Bind, $Port)
Write-Host '  Коды из «SMS» будут появляться ниже, в этом окне.  Остановить: Ctrl+C' -ForegroundColor Yellow
Write-Host ''

python -m uvicorn app.main:app --host $Bind --port $Port
