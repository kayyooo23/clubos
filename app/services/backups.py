"""Резервные копии базы. В базе баланс и история денег гостей, поэтому потеря базы это потеря денег клуба: копия делается по расписанию, проверяется (недоступная
для восстановления копия хуже, чем её отсутствие: на неё надеются) и хранится по схеме «много свежих, меньше старых». Работает и с SQLite (разработка, маленькие клубы),
и с PostgreSQL (боевой запуск): для SQLite используется встроенное копирование на ходу, для PostgreSQL pg_dump в «custom»-формате.

Файлы: club-ГГГГММДД-ЧЧММСС.sqlite3.gz или club-ГГГГММДД-ЧЧММСС.pgdump (время UTC). Рядом backup-status.json: чем кончилась последняя попытка; его читает сервер
и показывает в панели платформы, а если копия устарела или попытка не удалась, там появляется предупреждение. Копии содержат телефоны гостей: папка должна быть закрыта.
Копия на том же диске не спасает от потери диска: для этого есть --copy-to (вторая папка: сетевой диск, внешний диск) и в документации выгрузка в облако."""
import gzip
import json
import os
import re
import shutil
import sqlite3
import subprocess
import tempfile
from dataclasses import dataclass
from datetime import datetime, timedelta, timezone
from pathlib import Path

from sqlalchemy.engine import make_url

STATUS_FILE = "backup-status.json"
NAME_RE = re.compile(r"^club-(\d{8})-(\d{6})\.(sqlite3\.gz|pgdump)$")
KEY_TABLES = ("clubs", "accounts", "wallet_transactions")        # без них копия не считается годной


class BackupError(Exception):
    pass


@dataclass(frozen=True)
class BackupResult:
    file: Path
    size: int
    kind: str                       # sqlite | postgres
    copies: list[dict]              # куда ещё скопировано и получилось ли


def kind_of(db_url: str) -> str:
    return "sqlite" if db_url.startswith("sqlite") else "postgres"


def parse_name(name: str) -> datetime | None:
    m = NAME_RE.match(name)
    if not m:
        return None
    try:
        return datetime.strptime(m.group(1) + m.group(2), "%Y%m%d%H%M%S")
    except ValueError:
        return None


def new_name(now: datetime, kind: str) -> str:
    return f"club-{now:%Y%m%d-%H%M%S}." + ("sqlite3.gz" if kind == "sqlite" else "pgdump")


# ---------- какие копии хранить ----------
def select_to_keep(names: list[str], now: datetime, daily: int = 14, weekly: int = 8, monthly: int = 12) -> set[str]:
    """Хранятся: за последние `daily` суток самая поздняя копия каждых суток, за последние `weekly` недель самая поздняя копия каждой недели, за последние `monthly` месяцев
    самая поздняя копия каждого месяца. Самая свежая копия хранится всегда. Файлы с чужими именами сюда не попадают и не удаляются."""
    stamped = sorted(((parse_name(n), n) for n in names if parse_name(n)), reverse=True)
    keep: set[str] = set()
    if not stamped:
        return keep
    keep.add(stamped[0][1])
    seen_days: set = set()
    seen_weeks: set = set()
    seen_months: set = set()
    for stamp, name in stamped:                                   # от свежих к старым: в каждой «корзине» остаётся самая поздняя
        age = now - stamp
        day, week, month = stamp.date(), stamp.isocalendar()[:2], (stamp.year, stamp.month)
        if age <= timedelta(days=daily) and day not in seen_days:
            seen_days.add(day)
            keep.add(name)
        if age <= timedelta(weeks=weekly) and week not in seen_weeks:
            seen_weeks.add(week)
            keep.add(name)
        if age <= timedelta(days=31 * monthly) and month not in seen_months:
            seen_months.add(month)
            keep.add(name)
    return keep


def apply_retention(dest: Path, now: datetime, daily: int = 14, weekly: int = 8, monthly: int = 12) -> list[str]:
    names = [p.name for p in dest.iterdir() if p.is_file() and parse_name(p.name)]
    keep = select_to_keep(names, now, daily, weekly, monthly)
    removed = []
    for name in names:
        if name not in keep:
            (dest / name).unlink(missing_ok=True)
            removed.append(name)
    return removed


# ---------- PostgreSQL: где лежат pg_dump и pg_restore ----------
def pg_tool(name: str) -> str:
    """Утилита PostgreSQL: из PG_BIN, из PATH или (на ПК разработки) из C:\\clubos-tools. Версия pg_dump должна быть не ниже версии сервера базы."""
    exe = name + (".exe" if os.name == "nt" else "")
    for folder in (os.environ.get("PG_BIN"), r"C:\clubos-tools\pgsql\bin"):
        if folder and (Path(folder) / exe).exists():
            return str(Path(folder) / exe)
    found = shutil.which(name)
    if not found:
        raise BackupError(f"Не найдена утилита {name}: установите клиент PostgreSQL или задайте PG_BIN")
    return found


def _pg_args(url) -> tuple[list[str], dict]:
    args = ["-h", url.host or "localhost", "-p", str(url.port or 5432), "-U", url.username or "postgres"]
    env = {**os.environ}
    if url.password:
        env["PGPASSWORD"] = url.password
    return args, env


def _run(cmd: list[str], env: dict | None = None) -> str:
    p = subprocess.run(cmd, capture_output=True, text=True, env=env, timeout=3600)
    if p.returncode != 0:
        raise BackupError(f"{Path(cmd[0]).name}: {(p.stderr or p.stdout).strip()[:300]}")
    return p.stdout


# ---------- создание ----------
def _dump_sqlite(url, target: Path) -> None:
    source_path = Path(url.database)
    if not source_path.exists():
        raise BackupError(f"Файл базы {source_path} не найден")
    with tempfile.TemporaryDirectory() as tmp:
        raw = Path(tmp) / "snapshot.db"
        src = sqlite3.connect(source_path)
        dst = sqlite3.connect(raw)
        try:
            src.backup(dst)                                       # согласованный снимок на ходу, сервер можно не останавливать
        finally:
            dst.close()
            src.close()
        with open(raw, "rb") as f_in, gzip.open(target, "wb", compresslevel=6) as f_out:
            shutil.copyfileobj(f_in, f_out)


def _dump_postgres(url, target: Path) -> None:
    args, env = _pg_args(url)
    _run([pg_tool("pg_dump"), *args, "-d", url.database, "-F", "c", "--no-owner", "--no-privileges", "-f", str(target)], env)


def verify(file: Path, kind: str | None = None) -> None:
    """Копия годна, если её можно открыть и в ней есть главные таблицы. Для SQLite ещё проверяется целостность базы. kind: sqlite или postgres (по умолчанию по расширению файла)."""
    if (kind == "postgres") if kind else file.suffix == ".pgdump":
        listing = _run([pg_tool("pg_restore"), "--list", str(file)])
        missing = [t for t in KEY_TABLES if not re.search(rf"TABLE\s+public\s+{t}\b", listing)]
        if missing:
            raise BackupError(f"В копии нет таблиц: {', '.join(missing)}")
        return
    with tempfile.TemporaryDirectory() as tmp:
        raw = Path(tmp) / "check.db"
        try:
            with gzip.open(file, "rb") as f_in, open(raw, "wb") as f_out:
                shutil.copyfileobj(f_in, f_out)
        except (OSError, EOFError) as e:
            raise BackupError(f"Копия не открывается: {e}") from e
        con = sqlite3.connect(raw)
        try:
            if con.execute("PRAGMA integrity_check").fetchone()[0] != "ok":
                raise BackupError("База в копии повреждена (integrity_check)")
            tables = {r[0] for r in con.execute("select name from sqlite_master where type='table'")}
            missing = [t for t in KEY_TABLES if t not in tables]
            if missing:
                raise BackupError(f"В копии нет таблиц: {', '.join(missing)}")
        finally:
            con.close()


def make_backup(db_url: str, dest: Path, now: datetime, copy_to: list[Path] | None = None) -> BackupResult:
    """Снимает копию во временный файл, проверяет её и только потом кладёт под настоящим именем: оборванная или битая копия настоящей не становится."""
    dest.mkdir(parents=True, exist_ok=True)
    url = make_url(db_url)
    kind = kind_of(db_url)
    final = dest / new_name(now, kind)
    part = dest / (final.name + ".part")
    try:
        (_dump_sqlite if kind == "sqlite" else _dump_postgres)(url, part)
        verify(part, kind)
        os.replace(part, final)
    except BaseException:
        part.unlink(missing_ok=True)
        raise
    copies = []
    for folder in copy_to or []:
        try:
            Path(folder).mkdir(parents=True, exist_ok=True)
            shutil.copy2(final, Path(folder) / final.name)
            copies.append({"path": str(folder), "ok": True})
        except OSError as e:
            copies.append({"path": str(folder), "ok": False, "error": str(e)[:200]})
    return BackupResult(final, final.stat().st_size, kind, copies)


# ---------- состояние ----------
def write_status(dest: Path, now: datetime, ok: bool, message: str, result: BackupResult | None = None, removed: list[str] | None = None) -> None:
    dest.mkdir(parents=True, exist_ok=True)
    data = {"at": now.replace(tzinfo=timezone.utc).isoformat(), "ok": ok, "message": message,
            "file": result.file.name if result else None, "size": result.size if result else None, "kind": result.kind if result else None,
            "copies": result.copies if result else [], "removed": removed or []}
    tmp = dest / (STATUS_FILE + ".tmp")
    tmp.write_text(json.dumps(data, ensure_ascii=False), encoding="utf-8")
    os.replace(tmp, dest / STATUS_FILE)


def read_status(dest: Path) -> dict | None:
    try:
        return json.loads((dest / STATUS_FILE).read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return None


def list_backups(dest: Path, limit: int = 10) -> list[dict]:
    """Самые свежие копии в папке: имя, время (UTC) и размер."""
    if not dest.is_dir():
        return []
    rows = [(parse_name(p.name), p) for p in dest.iterdir() if p.is_file() and parse_name(p.name)]
    rows.sort(key=lambda r: r[0], reverse=True)
    return [{"name": p.name, "at": stamp.isoformat(), "size": p.stat().st_size} for stamp, p in rows[:limit]]


# ---------- восстановление ----------
def restore(file: Path, target_url: str, force: bool = False) -> dict[str, int]:
    """Разворачивает копию в целевую базу и возвращает число строк в главных таблицах. SQLite: файл назначения не перезаписывается без force.
    PostgreSQL: база должна уже существовать (createdb), её таблицы заменяются копией. Рабочую базу таким способом не восстанавливайте, не остановив сервер."""
    url = make_url(target_url)
    if file.suffix == ".pgdump":
        if kind_of(target_url) != "postgres":
            raise BackupError("Копия PostgreSQL разворачивается только в PostgreSQL")
        args, env = _pg_args(url)
        _run([pg_tool("pg_restore"), *args, "-d", url.database, "--clean", "--if-exists", "--no-owner", "--no-privileges", str(file)], env)
        counts_cmd = [pg_tool("psql"), *args, "-d", url.database, "-At", "-c", " union all ".join(f"select '{t}', count(*) from {t}" for t in KEY_TABLES)]
        out = _run(counts_cmd, env)
        return {line.split("|")[0]: int(line.split("|")[1]) for line in out.strip().splitlines()}
    if kind_of(target_url) != "sqlite":
        raise BackupError("Копия SQLite разворачивается только в SQLite")
    target = Path(url.database)
    if target.exists() and not force:
        raise BackupError(f"Файл {target} уже есть: укажите --force, чтобы заменить его")
    target.parent.mkdir(parents=True, exist_ok=True)
    tmp = target.with_name(target.name + ".restoring")
    try:
        with gzip.open(file, "rb") as f_in, open(tmp, "wb") as f_out:
            shutil.copyfileobj(f_in, f_out)
        os.replace(tmp, target)
    finally:
        tmp.unlink(missing_ok=True)
    con = sqlite3.connect(target)
    try:
        return {t: con.execute(f"select count(*) from {t}").fetchone()[0] for t in KEY_TABLES}
    finally:
        con.close()
