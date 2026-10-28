<#
.SYNOPSIS
  Собирает нативную часть клиента (clubos_native.dll, C++20) и запускает её тесты.
.DESCRIPTION
  Нужны Visual Studio (или Build Tools) с компонентом «Разработка классических приложений на C++»; CMake берётся из PATH или из Visual Studio.
  Результат: client\ClubOS.Native\out\clubos_native.dll (его подхватывают dotnet build и deploy\publish.ps1).
  При первой настройке тесты скачивают GoogleTest (лицензия BSD) в папку build\.
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [switch]$SkipTests,
    [switch]$Clean
)
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$build = Join-Path $here 'build'

function Find-CMake {
    $cmd = Get-Command cmake -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (Test-Path $vswhere) {
        foreach ($dir in (& $vswhere -all -products * -property installationPath)) {
            $c = Join-Path $dir 'Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe'
            if (Test-Path $c) { return $c }
        }
    }
    throw 'CMake не найден: установите Visual Studio с компонентом C++ или поставьте CMake.'
}

# какая версия Visual Studio с компилятором C++ есть: генератор называется по ней
function Find-Generator {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path $vswhere)) { throw 'Не найден vswhere: нужна Visual Studio или Build Tools.' }
    $found = & $vswhere -all -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property catalog_productLineVersion
    if ($found -contains '2022') { return 'Visual Studio 17 2022' }
    throw "Не найдена Visual Studio 2022 с компилятором C++ (найдено: $($found -join ', '))."
}

if ($Clean -and (Test-Path $build)) { Remove-Item $build -Recurse -Force }
$cmake = Find-CMake
$tests = if ($SkipTests) { 'OFF' } else { 'ON' }
Write-Host "CMake: $cmake" -ForegroundColor Cyan
& $cmake -S $here -B $build -G (Find-Generator) -A x64 "-DCLUBOS_BUILD_TESTS=$tests"
if ($LASTEXITCODE -ne 0) { throw 'Настройка CMake не удалась' }
& $cmake --build $build --config $Configuration --parallel
if ($LASTEXITCODE -ne 0) { throw 'Сборка не удалась' }
if (-not $SkipTests) {
    $ctest = Join-Path (Split-Path $cmake) 'ctest.exe'
    & $ctest --test-dir $build -C $Configuration --output-on-failure
    if ($LASTEXITCODE -ne 0) { throw 'Тесты нативной части не прошли' }
}
$dll = Join-Path $here 'out\clubos_native.dll'
if (-not (Test-Path $dll)) { throw "Сборка прошла, но $dll не появился" }
Write-Host ("Готово: $dll ({0:N0} КБ)" -f ((Get-Item $dll).Length / 1KB)) -ForegroundColor Green
