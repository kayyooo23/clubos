<#
.SYNOPSIS
  Устанавливает клиент ClubOS на клубный ПК: служба Windows + автозапуск экрана блокировки.
.DESCRIPTION
  Запускать от имени администратора из папки dist (рядом лежат Service\, Overlay\ и version.txt).
  Что делает:
    1. копирует программу в C:\Program Files\ClubOS\versions\<версия>: каждая версия живёт в своей папке, это нужно автообновлению
       (новая версия ложится рядом, а при неудаче прежняя остаётся нетронутой и возвращается сама);
    2. пишет C:\ProgramData\ClubOS\agent.json (адрес сервера, КОД ПОДКЛЮЧЕНИЯ или ключ этого ПК, гостевой пользователь и режим очистки) и закрывает
       папку правами: читать могут только система и администраторы — гость ключ и код прочитать не должен;
       с кодом подключения ПК при первом запуске службы сам подключается к клубу и получает свой ключ (вводить ключи руками не нужно);
    3. создаёт службу ClubOSAgent (автозапуск, перезапуск при сбое) и запускает её;
    4. создаёт задачу планировщика «ClubOS Overlay»: при входе гостевого пользователя запускает экран блокировки
       (--kiosk), а если он закрылся или упал — перезапускает.
  Ничего не удаляет и не меняет за пределами перечисленного. Для предварительного просмотра: -WhatIf.
.EXAMPLE
  .\install.ps1 -Server https://clubos.example.com -EnrollCode K7M2-9QXA-4TPD -GuestUser guest -WhatIf
.EXAMPLE
  .\install.ps1 -Server https://clubos.example.com -PcKey <ключ ПК из админки> -GuestUser guest -WhatIf
#>
[CmdletBinding(SupportsShouldProcess = $true, DefaultParameterSetName = 'Code')]
param(
    [Parameter(Mandatory)][string]$Server,       # адрес сервера ClubOS, лучше https://
    [Parameter(Mandatory, ParameterSetName = 'Code')][string]$EnrollCode,   # код подключения из админки (раздел «Коды подключения»): один на все ПК зала
    [Parameter(Mandatory, ParameterSetName = 'Key')][string]$PcKey,         # или ключ ЭТОГО ПК (показывается один раз при создании ПК в админке вручную)
    [string]$GuestUser = 'guest',                # локальный пользователь Windows, под которым играют гости
    [ValidateSet('on', 'dry-run', 'off')][string]$Cleanup = 'on',   # очистка ПК после сессии: on закрывает программы и удаляет данные гостя, dry-run только пишет в журнал
    [string]$GamesRoot,                          # игровая папка этого ПК (например D:\Games или библиотека Steam): ПК будет участвовать в синхронизации игр
    [int]$SyncPort = 47900,                      # порт, на котором ПК раздаёт файлы игр другим ПК клуба (открывается только для локальной сети)
    [string]$InstallDir = "$env:ProgramFiles\ClubOS"
)
$ErrorActionPreference = 'Stop'

$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Запустите скрипт от имени администратора.'
}
if ($Server -notmatch '^https://' -and $Server -notmatch '^http://(localhost|127\.0\.0\.1)') {
    Write-Warning 'Сервер указан без https: ключ ПК и пароли гостей пойдут по сети открытым текстом.'
}
$src = $PSScriptRoot
foreach ($d in 'Service', 'Overlay') { if (-not (Test-Path (Join-Path $src $d))) { throw "Не найдена папка $d рядом со скриптом" } }
$versionFile = Join-Path $src 'version.txt'
if (-not (Test-Path $versionFile)) { throw 'Нет version.txt рядом со скриптом: соберите dist новым publish.ps1' }
$Version = (Get-Content $versionFile -Raw).Trim()
if ($Version -notmatch '^\d{1,4}\.\d{1,4}\.\d{1,4}(\.\d{1,4})?$') { throw "В version.txt не версия: '$Version'" }
$versionDir = Join-Path $InstallDir "versions\$Version"

# 1. программа (повторная установка той же версии: сначала останавливаем работающие службу и оверлей, иначе файлы заняты)
if ($PSCmdlet.ShouldProcess($versionDir, 'Скопировать файлы клиента')) {
    if (Get-Service ClubOSAgent -ErrorAction SilentlyContinue) { Stop-Service ClubOSAgent -Force -ErrorAction SilentlyContinue }
    Get-Process ClubOS.Overlay -ErrorAction SilentlyContinue | Stop-Process -Force
    New-Item -ItemType Directory -Force -Path $versionDir | Out-Null
    Copy-Item (Join-Path $src 'Service') $versionDir -Recurse -Force
    Copy-Item (Join-Path $src 'Overlay') $versionDir -Recurse -Force
    Copy-Item $versionFile $versionDir -Force
    # папки прежней раскладки (до автообновления) больше не нужны
    foreach ($legacy in 'Service', 'Overlay') {
        $old = Join-Path $InstallDir $legacy
        if (Test-Path $old) { Remove-Item $old -Recurse -Force; Write-Host "Удалена старая папка $old" }
    }
}

# 2. настройки с ключом ПК — только для системы и администраторов
$dataDir = Join-Path $env:ProgramData 'ClubOS'
if ($PSCmdlet.ShouldProcess($dataDir, 'Создать agent.json и ограничить доступ')) {
    New-Item -ItemType Directory -Force -Path $dataDir | Out-Null
    icacls $dataDir /inheritance:r /grant:r 'SYSTEM:(OI)(CI)F' 'BUILTIN\Administrators:(OI)(CI)F' | Out-Null
    # guest_user ограничивает очистку: она действует только в сессии этого пользователя Windows и никогда в сессии администратора
    $settings = @{ server = $Server.TrimEnd('/'); guest_user = $GuestUser; cleanup = $Cleanup }
    if ($GamesRoot) { $settings.games_root = $GamesRoot; $settings.sync_port = $SyncPort }
    if ($PSCmdlet.ParameterSetName -eq 'Key') { $settings.pc_key = $PcKey } else { $settings.enroll_code = $EnrollCode }
    $settings | ConvertTo-Json | Set-Content (Join-Path $dataDir 'agent.json') -Encoding UTF8
}

# 2а. синхронизация игр: порт раздачи файлов открывается только для локальной подсети (правило по порту, а не по программе: путь программы меняется при обновлении)
if ($GamesRoot) {
    if (-not (Test-Path $GamesRoot)) { Write-Warning "Игровая папка $GamesRoot не найдена: создайте её или поправьте games_root в agent.json." }
    if ($PSCmdlet.ShouldProcess("TCP $SyncPort", 'Открыть порт синхронизации игр для локальной сети')) {
        Remove-NetFirewallRule -DisplayName 'ClubOS Sync' -ErrorAction SilentlyContinue
        New-NetFirewallRule -DisplayName 'ClubOS Sync' -Direction Inbound -Protocol TCP -LocalPort $SyncPort -RemoteAddress LocalSubnet -Action Allow -Profile Any | Out-Null
    }
}

# 3. служба
$svc = 'ClubOSAgent'
$exe = Join-Path $versionDir 'Service\ClubOS.Service.exe'
if ($PSCmdlet.ShouldProcess($svc, 'Создать и запустить службу')) {
    if (Get-Service $svc -ErrorAction SilentlyContinue) { Stop-Service $svc -Force; sc.exe delete $svc | Out-Null; Start-Sleep 2 }
    New-Service -Name $svc -BinaryPathName "`"$exe`"" -DisplayName 'ClubOS Agent' `
        -Description 'Связь ПК с сервером ClubOS и блокировка' -StartupType Automatic | Out-Null
    sc.exe failure $svc reset= 86400 actions= restart/5000/restart/5000/restart/30000 | Out-Null
    Start-Service $svc
}

# 4. экран блокировки при входе гостя, с перезапуском
$overlay = Join-Path $versionDir 'Overlay\ClubOS.Overlay.exe'
if ($PSCmdlet.ShouldProcess("ClubOS Overlay ($GuestUser)", 'Создать задачу планировщика')) {
    $action = New-ScheduledTaskAction -Execute $overlay -Argument '--kiosk'
    $trigger = New-ScheduledTaskTrigger -AtLogOn -User $GuestUser
    $settings = New-ScheduledTaskSettingsSet -RestartCount 999 -RestartInterval (New-TimeSpan -Minutes 1) `
        -ExecutionTimeLimit ([TimeSpan]::Zero) -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
    $taskPrincipal = New-ScheduledTaskPrincipal -UserId $GuestUser -LogonType Interactive -RunLevel Limited
    Register-ScheduledTask -TaskName 'ClubOS Overlay' -Action $action -Trigger $trigger -Settings $settings `
        -Principal $taskPrincipal -Force | Out-Null
}

Write-Host 'Готово. Дальше:' -ForegroundColor Green
Write-Host "  1) .\harden-guest.ps1 -User $GuestUser   — запретить гостю диспетчер задач и т.п. (сначала с -WhatIf)"
Write-Host '  2) включите автовход пользователя гостя и перезагрузите ПК'
