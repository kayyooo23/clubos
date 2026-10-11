<#
.SYNOPSIS
  Ограничивает гостевого пользователя Windows: нет диспетчера задач, командной строки, редактора реестра и т.д.
.DESCRIPTION
  Экран блокировки ClubOS перехватывает Win, Alt+Tab и подобное, но Ctrl+Alt+Del из программы перехватить НЕЛЬЗЯ.
  Поэтому то, что можно открыть из его меню (диспетчер задач, блокировка, смена пароля), закрывается политиками Windows.
  Скрипт правит реестр ТОЛЬКО указанного пользователя (его профиль), администраторов и систему не трогает.
  Запускать от имени администратора. Сначала посмотрите, что будет сделано:  .\harden-guest.ps1 -User guest -WhatIf
  Отмена: .\harden-guest.ps1 -User guest -Undo

  ВАЖНО: ограничения действуют и на режим сотрудника (рабочий стол открывается под тем же пользователем Windows).
  Для настройки ПК используйте отдельную учётную запись администратора Windows.
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter(Mandatory)][string]$User,
    [switch]$Undo
)
$ErrorActionPreference = 'Stop'

$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Запустите скрипт от имени администратора.' }

$sid = (New-Object Security.Principal.NTAccount($User)).Translate([Security.Principal.SecurityIdentifier]).Value
$hive = "Registry::HKEY_USERS\$sid"
$loadedHere = $false
if (-not (Test-Path $hive)) {                      # пользователь сейчас не вошёл — подключаем его файл настроек
    $profile = (Get-CimInstance Win32_UserProfile | Where-Object SID -eq $sid).LocalPath
    if (-not $profile) { throw "У пользователя $User нет профиля: войдите им в Windows один раз." }
    if ($PSCmdlet.ShouldProcess("$profile\NTUSER.DAT", 'Подключить кусты реестра пользователя')) {
        reg.exe load "HKU\ClubOS_$sid" "$profile\NTUSER.DAT" | Out-Null
        $hive = "Registry::HKEY_USERS\ClubOS_$sid"
        $loadedHere = $true
    }
}

# что закрываем: путь, имя значения
$policies = @(
    @{ Path = 'Software\Microsoft\Windows\CurrentVersion\Policies\System';   Name = 'DisableTaskMgr' },           # диспетчер задач
    @{ Path = 'Software\Microsoft\Windows\CurrentVersion\Policies\System';   Name = 'DisableLockWorkstation' },   # Win+L / блокировка
    @{ Path = 'Software\Microsoft\Windows\CurrentVersion\Policies\System';   Name = 'DisableChangePassword' },    # смена пароля Windows
    @{ Path = 'Software\Microsoft\Windows\CurrentVersion\Policies\System';   Name = 'DisableRegistryTools' },     # regedit
    @{ Path = 'Software\Microsoft\Windows\CurrentVersion\Policies\Explorer'; Name = 'NoRun' },                    # «Выполнить»
    @{ Path = 'Software\Microsoft\Windows\CurrentVersion\Policies\Explorer'; Name = 'NoControlPanel' },           # панель управления и параметры
    @{ Path = 'Software\Policies\Microsoft\Windows\System';                  Name = 'DisableCMD' }                # командная строка
)
try {
    foreach ($p in $policies) {
        $full = Join-Path $hive $p.Path
        if ($Undo) {
            if ($PSCmdlet.ShouldProcess("$($p.Path)\$($p.Name)", 'Убрать ограничение')) {
                Remove-ItemProperty -Path $full -Name $p.Name -ErrorAction SilentlyContinue
            }
        } elseif ($PSCmdlet.ShouldProcess("$($p.Path)\$($p.Name)", 'Включить ограничение')) {
            New-Item -Path $full -Force | Out-Null
            New-ItemProperty -Path $full -Name $p.Name -Value 1 -PropertyType DWord -Force | Out-Null
        }
    }
} finally {
    if ($loadedHere) {
        [gc]::Collect(); [gc]::WaitForPendingFinalizers()
        reg.exe unload "HKU\ClubOS_$sid" | Out-Null
    }
}
Write-Host ($(if ($Undo) { 'Ограничения сняты.' } else { 'Ограничения применены. Они вступят в силу при следующем входе пользователя.' })) -ForegroundColor Green
