"""Подключение ПК по коду. Раньше на каждый ПК сотрудник создавал ПК в админке, получал ключ (показывается один раз) и вписывал его при установке:
для тридцати ПК это тридцать ключей руками, а клонированный образ получал один и тот же ключ на всех. Теперь:
  • сотрудник создаёт один код подключения (срок, сколько ПК можно подключить, зал для новых ПК);
  • установщик ставит клиент с этим кодом, и при первом запуске служба сама просит сервер подключить её (POST /api/agent/enroll);
  • сервер создаёт ПК (название по имени компьютера, зал по коду) и отдаёт ключ только этой службе, она сохраняет его у себя;
  • ПК привязан к железу (machine_id): тот же компьютер, подключаясь ещё раз (переустановка), получает новый ключ, а не второй ПК в списке.
Код проверяется только по хешу; неверные коды считаются и ограничиваются по адресу."""
import re
import secrets
from datetime import datetime, timedelta

from sqlalchemy import or_, select, update
from sqlalchemy.orm import Session as DB

from ..errors import ServiceError
from ..models import Club, EnrollCode, PC
from ..schemas import EnrollCodeIn, EnrollIn
from ..security import hash_secret, new_secret
from . import audit, ratelimit, zones

ALPHABET = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"      # без похожих знаков: I, O, 0, 1
BAD_CODE = "Код подключения недействителен: он неверный, истёк, отозван или уже использован"


def new_code() -> str:
    """Код вида K7M2-9QXA-4TPD: 12 знаков из 32 (60 бит), его удобно прочитать и набрать на ПК."""
    raw = "".join(secrets.choice(ALPHABET) for _ in range(12))
    return "-".join(raw[i:i + 4] for i in range(0, 12, 4))


def normalize(code: str) -> str:
    return re.sub(r"[^A-Z0-9]", "", code.upper())


def code_out(c: EnrollCode, now: datetime) -> dict:
    expired = c.expires_at <= now
    exhausted = c.max_uses is not None and c.used_count >= c.max_uses
    status = "revoked" if c.revoked_at else "expired" if expired else "used_up" if exhausted else "active"
    return {"id": c.id, "hint": c.code_hint, "label": c.label, "zone_id": c.zone_id, "created_at": c.created_at, "expires_at": c.expires_at,
            "max_uses": c.max_uses, "used_count": c.used_count, "status": status}


def create_code(db: DB, club: Club, staff, data: EnrollCodeIn, now: datetime) -> tuple[EnrollCode, str]:
    if data.zone_id is not None:
        zones.get_zone(db, club.id, data.zone_id)             # зал должен быть из этого клуба
    code = new_code()
    row = EnrollCode(club_id=club.id, code_hash=hash_secret(normalize(code)), code_hint=normalize(code)[-4:], label=data.label, zone_id=data.zone_id,
                     created_by=staff.id, created_at=now, expires_at=now + timedelta(days=data.days), max_uses=data.max_uses)
    db.add(row)
    db.flush()
    return row, code


def list_codes(db: DB, club_id: int, now: datetime) -> list[dict]:
    return [code_out(c, now) for c in db.scalars(select(EnrollCode).where(EnrollCode.club_id == club_id).order_by(EnrollCode.id.desc()).limit(100))]


def revoke_code(db: DB, club_id: int, code_id: int, now: datetime) -> EnrollCode:
    c = db.scalar(select(EnrollCode).where(EnrollCode.id == code_id, EnrollCode.club_id == club_id))
    if not c:
        raise ServiceError(404, "Код не найден")
    if not c.revoked_at:
        c.revoked_at = now
    return c


def _pick_name(db: DB, club_id: int, hostname: str | None) -> str:
    """Название ПК по имени компьютера (только буквы, цифры, дефис и подчёркивание), уникальное в клубе: DESKTOP-AB12, DESKTOP-AB12-2, ..."""
    base = re.sub(r"[^A-Za-z0-9_-]", "", hostname or "")[:40] or "PC"
    taken = set(db.scalars(select(PC.name).where(PC.club_id == club_id, PC.name.like(f"{base}%"))))
    if base not in taken:
        return base
    n = 2
    while f"{base}-{n}" in taken:
        n += 1
    return f"{base}-{n}"


def enroll(db: DB, data: EnrollIn, now: datetime, ip: str | None) -> dict:
    """ПК подключается по коду. Возвращает ключ (виден только в этом ответе, в базе его хеш), название ПК и клуба."""
    key_ip = ip or "unknown"
    ratelimit.enroll_failures.check(key_ip)
    code = db.scalar(select(EnrollCode).where(EnrollCode.code_hash == hash_secret(normalize(data.code))))
    club = db.get(Club, code.club_id) if code else None
    if not code or code.revoked_at or code.expires_at <= now or club.status != "active":
        ratelimit.enroll_failures.fail(key_ip)
        raise ServiceError(403, BAD_CODE)

    key = new_secret()
    pc = db.scalar(select(PC).where(PC.club_id == club.id, PC.machine_id == data.machine_id))
    if pc:                                               # тот же компьютер подключается снова (переустановка): новый ключ, второго ПК в списке нет; исчерпанный код этому не мешает
        pc.key_hash, pc.clone_alert_at = hash_secret(key), None
        audit.record(db, club.id, "pc_reenrolled", now, pc_id=pc.id, details=f"code={code.id}", ip=ip)
    else:
        # место по коду занимается условным обновлением: два ПК, подключающихся одновременно, не превысят число ПК в коде
        taken = db.execute(update(EnrollCode).where(EnrollCode.id == code.id, or_(EnrollCode.max_uses.is_(None), EnrollCode.used_count < EnrollCode.max_uses))
                           .values(used_count=EnrollCode.used_count + 1)).rowcount
        if taken != 1:
            db.rollback()
            ratelimit.enroll_failures.fail(key_ip)
            raise ServiceError(403, BAD_CODE)
        pc = PC(club_id=club.id, name=_pick_name(db, club.id, data.hostname), key_hash=hash_secret(key), machine_id=data.machine_id,
                enrolled_at=now, enroll_code_id=code.id)
        if code.zone_id is not None:
            zones.assign_pc(db, club.id, pc, code.zone_id)    # новые ПК сразу в нужном зале: цены и характеристики берутся у него
        db.add(pc)
        db.flush()
        audit.record(db, club.id, "pc_enrolled", now, pc_id=pc.id, details=f"code={code.id}", ip=ip)
    db.commit()
    return {"pc_id": pc.id, "pc_name": pc.name, "club_name": club.name, "zone": pc.zone, "key": key}
