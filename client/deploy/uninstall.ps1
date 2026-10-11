<#
.SYNOPSIS
  Убирает клиент ClubOS с ПК: останавливает и удаляет службу и задачу экрана блокировки.
.DESCRIPTION
  Папку C:\ProgramData\ClubOS (в ней ключ ПК) и файлы в C:\Program Files\ClubOS удаляет, только если указан -RemoveFiles.
  Предварительный просмотр: -WhatIf. Политики гостя снимаются отдельно: harden-guest.ps1 -Undo.
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$InstallDir = "$env:ProgramFiles\ClubOS",
    [switch]$RemoveFiles
)
$ErrorActionPreference = 'Stop'
$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Запустите скрипт от имени администратора.' }

if ($PSCmdlet.ShouldProcess('ClubOS Overlay', 'Удалить задачу планировщика')) {
    Get-Process ClubOS.Overlay -ErrorAction SilentlyContinue | Stop-Process -Force
    Unregister-ScheduledTask -TaskName 'ClubOS Overlay' -Confirm:$false -ErrorAction SilentlyContinue
}
if ($PSCmdlet.ShouldProcess('ClubOSAgent', 'Остановить и удалить службу')) {
    if (Get-Service ClubOSAgent -ErrorAction SilentlyContinue) {
        Stop-Service ClubOSAgent -Force -ErrorAction SilentlyContinue
        sc.exe delete ClubOSAgent | Out-Null
    }
}
if ($RemoveFiles) {
    foreach ($dir in @($InstallDir, (Join-Path $env:ProgramData 'ClubOS'))) {
        if ((Test-Path $dir) -and $PSCmdlet.ShouldProcess($dir, 'Удалить папку')) { Remove-Item $dir -Recurse -Force }
    }
}
Write-Host 'Готово.' -ForegroundColor Green
