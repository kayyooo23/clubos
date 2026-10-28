<#
.SYNOPSIS
  Локальный PostgreSQL для разработки и тестов ClubOS: переносимая сборка, без службы Windows и без прав администратора.
.DESCRIPTION
  .\scripts\pg_dev.ps1 install   скачать официальный архив с бинарниками (EnterpriseDB, источник postgresql.org) и распаковать, один раз
  .\scripts\pg_dev.ps1 start     создать кластер при первом запуске и запустить сервер (порт 5433), создать базы clubos_dev и clubos_test
  .\scripts\pg_dev.ps1 stop      остановить
  .\scripts\pg_dev.ps1 status    запущен ли
  .\scripts\pg_dev.ps1 reset     удалить данные и создать кластер заново
  Всё лежит в папке %USERPROFILE%\clubos-tools (меняется параметром -ToolsDir), в репозиторий ничего не попадает.
  Если в имени профиля есть не латинские буквы (например, C:\Users\Виктория), по умолчанию берётся C:\clubos-tools: PostgreSQL на Windows не создаёт
  кластер в пути с кириллицей («invalid byte sequence for encoding UTF8»).
  Только для разработки: доступ без пароля и только с этого компьютера, сброс на диск отключён ради скорости тестов.
  Адреса баз:  postgresql+psycopg://postgres@localhost:5433/clubos_dev   и   .../clubos_test
#>
param(
    [Parameter(Mandatory)][ValidateSet('install', 'start', 'stop', 'status', 'reset')][string]$Action,
    [string]$ToolsDir = $(if ($env:USERPROFILE -match '^[\x00-\x7F]+$') { Join-Path $env:USERPROFILE 'clubos-tools' } else { 'C:\clubos-tools' }),
    [int]$Port = 5433
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$zipName = 'postgresql-17.11-5-windows-x64-binaries.zip'
$zipUrl = "https://get.enterprisedb.com/postgresql/$zipName"
$pgHome = Join-Path $ToolsDir 'pgsql'
$bin = Join-Path $pgHome 'bin'
$data = Join-Path $ToolsDir 'pgdata'
$log = Join-Path $ToolsDir 'postgres.log'

function Tool($name) { Join-Path $bin "$name.exe" }
function Installed { Test-Path (Tool 'pg_ctl') }
function Running {
    # код возврата pg_ctl status: 0 сервер работает, 3 не работает; текст ответа зависит от языка системы, поэтому по тексту не определяем
    if (-not (Installed) -or -not (Test-Path (Join-Path $data 'PG_VERSION'))) { return $false }
    & (Tool 'pg_ctl') -D $data status *> $null
    return ($LASTEXITCODE -eq 0)
}

function Invoke-PgCtl([string[]]$PgArgs) {
    # Start-Process -Wait ждёт и всех потомков процесса, а сервер PostgreSQL потомок pg_ctl: вечное ожидание. Поэтому ждём только сам pg_ctl.
    # Вывод в файлы, а не в конвейер: демон унаследовал бы канал вывода, и PowerShell ждал бы его закрытия.
    $proc = Start-Process -FilePath (Tool 'pg_ctl') -ArgumentList $PgArgs -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput (Join-Path $ToolsDir 'pg_ctl.out') -RedirectStandardError (Join-Path $ToolsDir 'pg_ctl.err')
    if (-not $proc.WaitForExit(120000)) { throw 'pg_ctl не ответил за 2 минуты' }
}

function Do-Install {
    if (Installed) { Write-Host "PostgreSQL уже установлен: $pgHome"; return }
    New-Item -ItemType Directory -Force (Join-Path $ToolsDir 'download') | Out-Null
    $zip = Join-Path $ToolsDir "download\$zipName"
    if (-not (Test-Path $zip) -or (Get-Item $zip).Length -lt 300MB) {
        Write-Host "Скачиваю $zipUrl (около 360 МБ)..."
        Invoke-WebRequest -Uri $zipUrl -OutFile $zip -UseBasicParsing -TimeoutSec 1800
    }
    Write-Host 'Распаковываю...'
    $tmp = Join-Path $ToolsDir 'unpack'
    if (Test-Path $tmp) { Remove-Item $tmp -Recurse -Force }
    Expand-Archive -Path $zip -DestinationPath $tmp -Force
    Move-Item (Join-Path $tmp 'pgsql') $pgHome
    Remove-Item $tmp -Recurse -Force
    Write-Host "Готово: $pgHome"
}

function Do-Start {
    if (-not (Installed)) { Do-Install }
    if (-not (Test-Path (Join-Path $data 'PG_VERSION'))) {
        Write-Host 'Создаю кластер...'
        & (Tool 'initdb') -D $data -U postgres -A trust -E UTF8 --locale=C | Out-Null   # locale C: сортировка по байтам, как в SQLite
        Add-Content (Join-Path $data 'postgresql.conf') "`nfsync = off`nsynchronous_commit = off`nfull_page_writes = off`nlisten_addresses = 'localhost'`n"
    }
    if (Running) { Write-Host "Уже запущен (порт $Port)" } else {
        Invoke-PgCtl @('-D', "`"$data`"", '-l', "`"$log`"", '-o', "`"-p $Port`"", '-w', 'start')
        if (-not (Running)) { throw "PostgreSQL не запустился, смотрите $log" }
        Write-Host "Запущен, порт $Port"
    }
    foreach ($db in 'clubos_dev', 'clubos_test') {
        $exists = & (Tool 'psql') -h localhost -p $Port -U postgres -d postgres -tAc "SELECT 1 FROM pg_database WHERE datname = '$db'"
        if (-not $exists) { & (Tool 'createdb') -h localhost -p $Port -U postgres $db; Write-Host "База $db создана" }
    }
    Write-Host "postgresql+psycopg://postgres@localhost:$Port/clubos_dev"
    Write-Host "postgresql+psycopg://postgres@localhost:$Port/clubos_test"
}

function Do-Stop {
    if (Running) {
        Invoke-PgCtl @('-D', "`"$data`"", '-m', 'fast', '-w', 'stop')
        Write-Host 'Остановлен'
    } else { Write-Host 'Не запущен' }
}

switch ($Action) {
    'install' { Do-Install }
    'start'   { Do-Start }
    'stop'    { Do-Stop }
    'status'  { if (Running) { Write-Host "Запущен, порт $Port" } else { Write-Host 'Не запущен' } }
    'reset'   { Do-Stop; if (Test-Path $data) { Remove-Item $data -Recurse -Force }; Do-Start }
}
