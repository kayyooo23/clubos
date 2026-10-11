<#
.SYNOPSIS
  Устанавливает клиент ClubOS на клубный ПК: служба Windows + автозапуск экрана блокировки.
.DESCRIPTION
  Запускать от имени администратора из папки dist (рядом лежат Service\ и Overlay\).
  Что делает:
    1. копирует программу в C:\Program Files\ClubOS;
    2. пишет C:\ProgramData\ClubOS\agent.json (адрес сервера, КЛЮЧ ЭТОГО ПК, гостевой пользователь и режим очистки) и закрывает папку правами:
       читать могут только система и администраторы — гость ключ прочитать не должен;
    3. создаёт службу ClubOSAgent (автозапуск, перезапуск при сбое) и запускает её;
    4. создаёт задачу планировщика «ClubOS Overlay»: при входе гостевого пользователя запускает экран блокировки
       (--kiosk), а если он закрылся или упал — перезапускает.
  Ничего не удаляет и не меняет за пределами перечисленного. Для предварительного просмотра: -WhatIf.
.EXAMPLE
  .\install.ps1 -Server https://clubos.example.com -PcKey <ключ ПК из админки> -GuestUser guest -WhatIf
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter(Mandatory)][string]$Server,       # адрес сервера ClubOS, лучше https://
    [Parameter(Mandatory)][string]$PcKey,        # ключ ЭТОГО ПК (показывается один раз при создании ПК в админке)
    [string]$GuestUser = 'guest',                # локальный пользователь Windows, под которым играют гости
    [ValidateSet('on', 'dry-run', 'off')][string]$Cleanup = 'on',   # очистка ПК после сессии: on закрывает программы и удаляет данные гостя, dry-run только пишет в журнал
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

# 1. программа
if ($PSCmdlet.ShouldProcess($InstallDir, 'Скопировать файлы клиента')) {
    New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
    Copy-Item (Join-Path $src 'Service') $InstallDir -Recurse -Force
    Copy-Item (Join-Path $src 'Overlay') $InstallDir -Recurse -Force
}

# 2. настройки с ключом ПК — только для системы и администраторов
$dataDir = Join-Path $env:ProgramData 'ClubOS'
if ($PSCmdlet.ShouldProcess($dataDir, 'Создать agent.json и ограничить доступ')) {
    New-Item -ItemType Directory -Force -Path $dataDir | Out-Null
    icacls $dataDir /inheritance:r /grant:r 'SYSTEM:(OI)(CI)F' 'BUILTIN\Administrators:(OI)(CI)F' | Out-Null
    # guest_user ограничивает очистку: она действует только в сессии этого пользователя Windows и никогда в сессии администратора
    @{ server = $Server.TrimEnd('/'); pc_key = $PcKey; guest_user = $GuestUser; cleanup = $Cleanup } | ConvertTo-Json | Set-Content (Join-Path $dataDir 'agent.json') -Encoding UTF8
}

# 3. служба
$svc = 'ClubOSAgent'
$exe = Join-Path $InstallDir 'Service\ClubOS.Service.exe'
if ($PSCmdlet.ShouldProcess($svc, 'Создать и запустить службу')) {
    if (Get-Service $svc -ErrorAction SilentlyContinue) { Stop-Service $svc -Force; sc.exe delete $svc | Out-Null; Start-Sleep 2 }
    New-Service -Name $svc -BinaryPathName "`"$exe`"" -DisplayName 'ClubOS Agent' `
        -Description 'Связь ПК с сервером ClubOS и блокировка' -StartupType Automatic | Out-Null
    sc.exe failure $svc reset= 86400 actions= restart/5000/restart/5000/restart/30000 | Out-Null
    Start-Service $svc
}

# 4. экран блокировки при входе гостя, с перезапуском
$overlay = Join-Path $InstallDir 'Overlay\ClubOS.Overlay.exe'
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
