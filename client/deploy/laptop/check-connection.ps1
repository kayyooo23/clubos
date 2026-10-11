$ErrorActionPreference = 'Stop'
$cfgPath = Join-Path $PSScriptRoot 'agent.json'
if (-not (Test-Path $cfgPath)) { Write-Host 'Не найден файл agent.json рядом со скриптом.' -ForegroundColor Red; exit 1 }
$cfg = Get-Content $cfgPath -Raw | ConvertFrom-Json
$url = $cfg.server.TrimEnd('/')
Write-Host "Проверяю связь с сервером $url ..."
Write-Host ''

try {
    $page = Invoke-WebRequest -Uri "$url/admin/" -UseBasicParsing -TimeoutSec 6
    Write-Host ("[ОК]  Сервер отвечает (код {0})." -f $page.StatusCode) -ForegroundColor Green
} catch {
    Write-Host '[НЕТ] Сервер не отвечает.' -ForegroundColor Red
    Write-Host ('      Причина: ' + $_.Exception.Message)
    Write-Host ''
    Write-Host 'Что проверить:'
    Write-Host '  1. Окно сервера (run_lan.cmd) открыто на ПК-сервере?'
    Write-Host '  2. На ПК-сервере один раз выполнен allow_lan_port.cmd (от имени администратора)?'
    Write-Host '  3. Оба компьютера в одной сети? Не включена ли «гостевая сеть»/изоляция Wi-Fi на роутере?'
    Write-Host '  4. Адрес в agent.json ("server") верный? Блокнотом можно поправить.'
    exit 1
}

try {
    $state = Invoke-RestMethod -Uri "$url/api/agent/state" -Headers @{ 'X-PC-Key' = $cfg.pc_key } -TimeoutSec 6
    Write-Host ("[ОК]  Ключ этого ПК принят сервером. Состояние ПК: {0}." -f $state.mode) -ForegroundColor Green
    Write-Host ''
    Write-Host 'Всё готово: запускайте run-windowed.cmd.' -ForegroundColor Green
} catch {
    Write-Host '[НЕТ] Сервер отвечает, но ключ этого ПК не принят.' -ForegroundColor Red
    Write-Host '      Ключ мог быть заменён. Создайте/обновите ПК в админке или через scripts\add_pc.py и положите новый agent.json.'
    exit 1
}
