"""Парк ПК клуба: карта (зал и клетка), заметка, «на обслуживании», железо от клиента и сводка для дашборда.
Всё строго в пределах клуба."""
import json
from datetime import datetime, timedelta

from pydantic import ValidationError
from sqlalchemy import func, select
from sqlalchemy.orm import Session as DB

from .. import config
from ..errors import ServiceError, commit_or_409
from ..models import Account, Club, Order, PC, Session, WalletTransaction
from ..schemas import HardwareIn, LayoutIn, PCPatch
from . import sessions, shop


# ---------- правка ПК и карта ----------
def _cell_taken(db: DB, club_id: int, zone: str, x: int, y: int, *, except_id: int) -> bool:
    return db.scalar(select(PC.id).where(PC.club_id == club_id, PC.zone == zone, PC.pos_x == x, PC.pos_y == y,
                                         PC.id != except_id)) is not None


def update_pc(db: DB, club_id: int, pc_id: int, patch: PCPatch, now: datetime) -> PC:
    pc = sessions.get_pc(db, club_id, pc_id)
    fields = patch.model_dump(exclude_unset=True)
    if "name" in fields:
        if not fields["name"]:
            raise ServiceError(422, "Название ПК не может быть пустым")
        if db.scalar(select(PC.id).where(PC.club_id == club_id, PC.name == fields["name"], PC.id != pc.id)):
            raise ServiceError(409, "ПК с таким именем уже есть")
        pc.name = fields["name"]
    if "maintenance" in fields:
        if fields["maintenance"] and sessions.active_session(db, club_id, pc.id):
            raise ServiceError(409, "На ПК идёт сессия гостя. Сначала остановите её")
        pc.maintenance = bool(fields["maintenance"])
    if "note" in fields:
        pc.note = fields["note"] or None
    zone = fields.get("zone", pc.zone)
    x, y = fields.get("pos_x", pc.pos_x), fields.get("pos_y", pc.pos_y)
    if (x is None) != (y is None):
        raise ServiceError(422, "Клетка задаётся колонкой и рядом вместе")
    if x is not None and _cell_taken(db, club_id, zone, x, y, except_id=pc.id):
        raise ServiceError(409, "Эта клетка уже занята другим ПК")
    pc.zone, pc.pos_x, pc.pos_y = zone, x, y
    commit_or_409(db, "ПК с таким именем уже есть")
    return pc


def save_layout(db: DB, club_id: int, data: LayoutIn) -> int:
    """Расстановка всех ПК за раз. Если у двух ПК одна клетка или есть чужой номер, не сохраняется ничего."""
    ids = [i.id for i in data.items]
    if len(set(ids)) != len(ids):
        raise ServiceError(422, "Один и тот же ПК указан дважды")
    pcs = {p.id: p for p in db.scalars(select(PC).where(PC.club_id == club_id, PC.id.in_(ids)))}
    if len(pcs) != len(ids):
        raise ServiceError(404, "ПК не найден")
    for item in data.items:
        if (item.pos_x is None) != (item.pos_y is None):
            raise ServiceError(422, "Клетка задаётся колонкой и рядом вместе")
        pc = pcs[item.id]
        pc.zone, pc.pos_x, pc.pos_y = item.zone.strip(), item.pos_x, item.pos_y
    cells = [(p.zone, p.pos_x, p.pos_y) for p in db.scalars(select(PC).where(PC.club_id == club_id)) if p.pos_x is not None]
    if len(set(cells)) != len(cells):
        db.rollback()
        raise ServiceError(409, "Два ПК в одной клетке. Расстановка не сохранена")
    db.commit()
    return len(ids)


# ---------- железо ----------
def _stable_key(report: dict) -> dict:
    """Что считаем «конфигурацией»: процессор, память, видеокарта, состав дисков. Свободное место, IP и система не в счёт."""
    return {"cpu": report.get("cpu"), "ram_gb": report.get("ram_gb"), "gpu": report.get("gpu"),
            "disks": sorted((d["name"], d["total_gb"]) for d in report.get("disks", []))}


def set_hardware(db: DB, pc: PC, data: HardwareIn, now: datetime) -> bool:
    """Запоминает железо. Возвращает True, если конфигурация изменилась по сравнению с прошлым отчётом (первый отчёт не в счёт)."""
    report = data.model_dump()
    changed = False
    if pc.hw_json:
        try:
            changed = _stable_key(json.loads(pc.hw_json)) != _stable_key(report)
        except (ValueError, KeyError, TypeError):
            changed = False
    pc.hw_json = json.dumps(report, ensure_ascii=False)
    pc.hw_updated_at = now
    if changed:
        pc.hw_changed_at = now
    return changed


def hw_view(pc: PC) -> dict | None:
    if not pc.hw_json:
        return None
    try:
        return {**json.loads(pc.hw_json), "reported_at": pc.hw_updated_at}
    except ValueError:
        return None


def set_programs(db: DB, pc: PC, data, now: datetime) -> int:
    """Запоминает список программ, который прислала служба. Возвращает, сколько записей принято."""
    pc.programs_json = json.dumps(data.programs, ensure_ascii=False)
    pc.programs_at = now
    return len(data.programs)


def scan_requested(pc: PC) -> bool:
    """Администратор просил обновить список, и новый отчёт ещё не пришёл."""
    return pc.programs_wanted_at is not None and (pc.programs_at is None or pc.programs_wanted_at > pc.programs_at)


def request_programs_scan(db: DB, club_id: int, pc_id: int, now: datetime) -> PC:
    pc = sessions.get_pc(db, club_id, pc_id)
    pc.programs_wanted_at = now
    db.commit()
    return pc


def programs_view(pc: PC, now: datetime, *, icons: bool = True) -> dict:
    try:
        programs = json.loads(pc.programs_json) if pc.programs_json else []
    except ValueError:
        programs = []
    if not icons:
        programs = [{"name": p["name"], "path": p["path"], "has_icon": "icon" in p} for p in programs]
    online = bool(pc.last_seen and (now - pc.last_seen).total_seconds() < config.PC_ONLINE_SECONDS)
    return {"pc": {"id": pc.id, "name": pc.name, "online": online}, "reported_at": pc.programs_at,
            "pending": scan_requested(pc), "programs": programs}


def ack_hardware(db: DB, club_id: int, pc_id: int) -> PC:
    pc = sessions.get_pc(db, club_id, pc_id)
    pc.hw_changed_at = None
    db.commit()
    return pc


# ---------- дашборд ----------
def dashboard(db: DB, club: Club, now: datetime) -> dict:
    """Сводка на сегодня (сутки по времени клуба): загрузка зала, деньги, очередь заказов."""
    local = now + timedelta(hours=club.utc_offset)
    day_start = local.replace(hour=0, minute=0, second=0, microsecond=0) - timedelta(hours=club.utc_offset)
    sessions.close_expired(db, club.id, now)

    pcs = list(db.scalars(select(PC).where(PC.club_id == club.id)))
    online_after = now - timedelta(seconds=config.PC_ONLINE_SECONDS)
    online = sum(1 for p in pcs if p.last_seen and p.last_seen >= online_after)
    playing = len(sessions.active_sessions_by_pc(db, club.id))

    games = db.execute(select(func.count(Session.id), func.coalesce(func.sum(Session.cost), 0)).where(
        Session.club_id == club.id, Session.ended_at.is_not(None), Session.ended_at >= day_start)).one()
    shop_orders, shop_total = db.execute(select(func.count(Order.id), func.coalesce(func.sum(Order.total), 0)).where(
        Order.club_id == club.id, Order.status != "cancelled", Order.created_at >= day_start)).one()
    cash = db.scalar(select(func.coalesce(func.sum(WalletTransaction.amount), 0)).where(
        WalletTransaction.club_id == club.id, WalletTransaction.kind == "topup_cash", WalletTransaction.created_at >= day_start))
    new_guests = db.scalar(select(func.count(Account.id)).where(
        Account.club_id == club.id, Account.role == "guest", Account.created_at >= day_start))
    return {
        "since": day_start,
        "pcs_total": len(pcs), "pcs_online": online, "pcs_playing": playing,
        "pcs_maintenance": sum(1 for p in pcs if p.maintenance),
        "pcs_config_changed": sum(1 for p in pcs if p.hw_changed_at),
        "load_percent": round(playing * 100 / len(pcs)) if pcs else 0,
        "sessions_today": games[0], "games_revenue_today": int(games[1]),
        "shop_orders_today": shop_orders, "shop_revenue_today": int(shop_total),
        "cash_in_today": int(cash or 0), "new_guests_today": new_guests or 0,
        **shop.inbox(db, club.id),
    }
