"""Состояние самого сервера: жив ли он, соответствует ли схема базы коду, сколько клубов и ПК на связи, когда была последняя резервная копия.
Две вещи для двух читателей. health() для автоматики (проверка «жив ли сервер» у хостинга, Docker, внешнего мониторинга): ничего лишнего, без цифр о клубах.
status() для владельца платформы (панель, вкладка «Сервер»): подробности и предупреждения человеческим языком."""
import shutil
import time
from datetime import datetime
from pathlib import Path

from alembic.config import Config
from alembic.script import ScriptDirectory
from sqlalchemy import func, select, text
from sqlalchemy.engine import make_url
from sqlalchemy.orm import Session as DB

from .. import config
from ..models import Club, PC, Session
from . import backups

_STARTED = time.time()
_ROOT = Path(__file__).resolve().parents[2]
_head: tuple[str | None] | None = None


def schema_head() -> str | None:
    """Последняя миграция, которую знает этот код (None: папки с миграциями рядом нет)."""
    global _head
    if _head is None:
        try:
            _head = (ScriptDirectory.from_config(Config(str(_ROOT / "alembic.ini"))).get_current_head(),)
        except Exception:                                      # noqa: BLE001: нет alembic.ini или миграций (нестандартная поставка): проверку схемы просто пропускаем
            _head = (None,)
    return _head[0]


def schema_current(db: DB) -> str | None:
    """Какая миграция применена в базе (None: таблицы версий нет, например в тестах, где схема создаётся из моделей)."""
    try:
        return db.execute(text("select version_num from alembic_version")).scalar()
    except Exception:                                          # noqa: BLE001
        db.rollback()
        return None


def schema_state(db: DB) -> dict:
    current, head = schema_current(db), schema_head()
    state = "unknown" if current is None or head is None else "ok" if current == head else "outdated"
    return {"state": state, "current": current, "head": head}


def health(db: DB) -> tuple[bool, dict]:
    """(здоров ли сервер, тело ответа). Не здоров, если база недоступна или схема старше кода (забыли выполнить alembic upgrade head)."""
    try:
        db.execute(text("select 1"))
        db_ok = True
    except Exception:                                          # noqa: BLE001
        db.rollback()
        db_ok = False
    schema = schema_state(db)["state"] if db_ok else "unknown"
    return db_ok and schema != "outdated", {"ok": db_ok and schema != "outdated", "db": db_ok, "schema": schema}


def _db_size(db: DB) -> int | None:
    try:
        url = make_url(config.DB_URL)
        if url.drivername.startswith("sqlite"):
            return Path(url.database).stat().st_size if url.database and Path(url.database).exists() else None
        return int(db.execute(text("select pg_database_size(current_database())")).scalar())
    except Exception:                                          # noqa: BLE001
        db.rollback()
        return None


def backups_info(now: datetime) -> dict:
    """Последняя копия, список свежих копий и предупреждение, если с копиями что-то не так."""
    dest = Path(config.BACKUP_DIR)
    status = backups.read_status(dest)
    files = backups.list_backups(dest, 10)
    warning = None
    age_hours = None
    if files:
        age_hours = round((now - datetime.fromisoformat(files[0]["at"])).total_seconds() / 3600, 1)
    if not files:
        warning = "Резервных копий нет. Настройте ежедневный запуск scripts/backup.py (см. deploy/server/README.md)"
    elif status and not status.get("ok"):
        warning = f"Последняя попытка резервной копии неудачна: {status.get('message')}"
    elif age_hours is not None and age_hours > config.BACKUP_MAX_AGE_HOURS:
        warning = f"Последняя резервная копия сделана {int(age_hours)} ч назад: проверьте расписание"
    free = None
    try:
        probe = dest if dest.exists() else dest.parent
        free = shutil.disk_usage(probe).free
    except OSError:
        pass
    return {"dir": str(dest), "files": files, "last_status": status, "age_hours": age_hours, "warning": warning, "disk_free": free}


def status(db: DB, now: datetime) -> dict:
    clubs = db.scalar(select(func.count(Club.id))) or 0
    active_clubs = db.scalar(select(func.count(Club.id)).where(Club.status == "active")) or 0
    pcs_total = db.scalar(select(func.count(PC.id))) or 0
    online_since = now.timestamp() - config.PC_ONLINE_SECONDS
    pcs_online = sum(1 for last in db.scalars(select(PC.last_seen).where(PC.last_seen.is_not(None))) if last.timestamp() >= online_since)
    sessions = db.scalar(select(func.count(Session.id)).where(Session.ended_at.is_(None))) or 0
    ok, body = health(db)
    return {"time": now, "uptime_seconds": int(time.time() - _STARTED), "env": config.ENV, "health": body,
            "db": {"kind": "sqlite" if config.DB_URL.startswith("sqlite") else "postgres", "size_bytes": _db_size(db)},
            "schema": schema_state(db), "clubs": {"total": clubs, "active": active_clubs}, "pcs": {"total": pcs_total, "online": pcs_online},
            "sessions_active": sessions, "backups": backups_info(now)}
