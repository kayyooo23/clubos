"""Команды ПК от администратора: перезагрузить и выключить, одному ПК или сразу нескольким. Всё строго в пределах клуба.
Как это ходит: администратор ставит команду (статус pending); служба на ПК, опрашивая состояние раз в 2 секунды, получает её в ответе (commands),
выполняет и подтверждает результат (POST /api/agent/commands/{id}/result). Отдельного входящего соединения к ПК нет: клубный ПК за NAT, и сервер
в облаке до него не достучится, поэтому ПК забирает команды сам, как и сообщения администратора.
Защита: набор команд закрытый (KINDS), у команды короткий срок жизни (TTL), отдаётся она не более MAX_ATTEMPTS раз, перезагрузка при идущей
сессии гостя только с явным подтверждением (force). Повторную доставку клиент гасит по номеру команды (см. client/ClubOS.Service/CommandRunner)."""
import json
from datetime import datetime, timedelta

from sqlalchemy import select
from sqlalchemy.orm import Session as DB

from ..errors import ServiceError
from ..models import PC, PcCommand
from . import sessions

KINDS = ("reboot", "shutdown")
TTL_MINUTES = 10                 # через столько минут команда, которую ПК так и не выполнил, отменяется
MAX_ATTEMPTS = 3                 # сколько раз её отдавать ПК, если подтверждения нет
RESEND_AFTER_SECONDS = 30        # через сколько секунд без подтверждения отдать ту же команду ещё раз
OPEN = ("pending", "sent")
LABELS = {"reboot": "Перезагрузка", "shutdown": "Выключение"}


def command_out(c: PcCommand) -> dict:
    return {"id": c.id, "pc_id": c.pc_id, "kind": c.kind, "label": LABELS.get(c.kind, c.kind), "status": c.status, "attempts": c.attempts,
            "created_at": c.created_at, "sent_at": c.sent_at, "finished_at": c.finished_at, "result": c.result, "expires_at": c.expires_at}


def expire_old(db: DB, club_id: int, now: datetime) -> None:
    """Команды, которые ПК так и не выполнил за отведённое время, отменяются: перезагрузка, поставленная давно, никому не нужна."""
    for c in db.scalars(select(PcCommand).where(PcCommand.club_id == club_id, PcCommand.status.in_(OPEN), PcCommand.expires_at <= now)):
        c.status, c.finished_at, c.result = "cancelled", now, "Истёк срок: ПК не ответил вовремя"


def create(db: DB, club_id: int, staff_id: int, pc_id: int, kind: str, force: bool, now: datetime) -> tuple[PcCommand, bool]:
    """Ставит команду ПК. Возвращает (команда, создана_ли_новая): если такая же уже ждёт, новая не создаётся."""
    if kind not in KINDS:
        raise ServiceError(422, "Неизвестная команда")
    pc = sessions.get_pc(db, club_id, pc_id)
    expire_old(db, club_id, now)
    same = db.scalar(select(PcCommand).where(PcCommand.pc_id == pc.id, PcCommand.kind == kind, PcCommand.status.in_(OPEN)))
    if same:
        return same, False
    if not force and sessions.active_session(db, club_id, pc.id):
        raise ServiceError(409, f"На {pc.name} идёт сессия гостя: {LABELS[kind].lower()} прервёт игру. Остановите сессию или подтвердите")
    cmd = PcCommand(club_id=club_id, pc_id=pc.id, kind=kind, payload=json.dumps({"force": force}), status="pending", created_by=staff_id,
                    created_at=now, expires_at=now + timedelta(minutes=TTL_MINUTES))
    db.add(cmd)
    db.flush()
    return cmd, True


def deliver(db: DB, pc: PC, now: datetime) -> list[dict]:
    """Команды, которые ПК должен выполнить: новые и отданные, но не подтверждённые за RESEND_AFTER_SECONDS (не более MAX_ATTEMPTS раз). Коммитит вызывающий."""
    out = []
    resend_before = now - timedelta(seconds=RESEND_AFTER_SECONDS)
    for c in db.scalars(select(PcCommand).where(PcCommand.pc_id == pc.id, PcCommand.status.in_(OPEN), PcCommand.expires_at > now).order_by(PcCommand.id)):
        if c.status == "sent" and (c.sent_at > resend_before or c.attempts >= MAX_ATTEMPTS):
            continue
        c.status, c.sent_at, c.attempts = "sent", now, c.attempts + 1
        out.append({"id": c.id, "kind": c.kind, "payload": json.loads(c.payload or "{}")})
    return out


def report(db: DB, pc: PC, command_id: int, ok: bool, message: str | None, now: datetime) -> PcCommand:
    """ПК сообщает результат. Повторное сообщение о той же команде ничего не меняет (ответ мог потеряться по дороге)."""
    c = db.scalar(select(PcCommand).where(PcCommand.id == command_id, PcCommand.pc_id == pc.id))
    if not c:
        raise ServiceError(404, "Команда не найдена")
    if c.status in OPEN:
        c.status, c.finished_at, c.result = ("done" if ok else "failed"), now, (message or "").strip()[:300] or None
    return c


def cancel(db: DB, club_id: int, command_id: int, now: datetime) -> PcCommand:
    c = db.scalar(select(PcCommand).where(PcCommand.id == command_id, PcCommand.club_id == club_id))
    if not c:
        raise ServiceError(404, "Команда не найдена")
    if c.status not in OPEN:
        raise ServiceError(409, "Команда уже закрыта")
    c.status, c.finished_at, c.result = "cancelled", now, "Отменена администратором"
    return c


def recent_for_pc(db: DB, club_id: int, pc_id: int, limit: int = 10) -> list[PcCommand]:
    return list(db.scalars(select(PcCommand).where(PcCommand.club_id == club_id, PcCommand.pc_id == pc_id).order_by(PcCommand.id.desc()).limit(limit)))


def latest_by_pc(db: DB, club_id: int, now: datetime) -> dict[int, PcCommand]:
    """Последняя команда каждого ПК за последний час: админка показывает её в панели ПК («Перезагрузка: выполнена»)."""
    out: dict[int, PcCommand] = {}
    for c in db.scalars(select(PcCommand).where(PcCommand.club_id == club_id, PcCommand.created_at > now - timedelta(hours=1)).order_by(PcCommand.id)):
        out[c.pc_id] = c
    return out
