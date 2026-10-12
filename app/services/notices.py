"""Сообщения администратора гостю на экран ПК. Всё строго в пределах клуба.
Администратор выбирает ПК в админке и пишет любой текст (например, «Пожалуйста, постарайтесь вести себя потише»). Сервер хранит
сообщение, а служба на ПК забирает его с очередным опросом состояния: пока сообщение свежее (FRESH_SECONDS), оно приходит в каждом
ответе, и клиент сам показывает его один раз (по номеру). Так сообщение не теряется, если у ПК на секунду пропала связь или
перезапустился оверлей, и не висит вечно, если ПК был выключен."""
from datetime import datetime, timedelta

from sqlalchemy import select
from sqlalchemy.orm import Session as DB

from ..errors import ServiceError
from ..models import Account, Club, PC, PcNotice

MAX_LEN = 300
FRESH_SECONDS = 120        # столько сообщение отдаётся ПК; служба опрашивает раз в 2 секунды
MAX_PER_STATE = 3          # за раз ПК получает не больше трёх последних, чтобы поток сообщений не завалил экран
RECENT_MINUTES = 10        # последнее сообщение каждого ПК админка показывает в панели ещё столько минут


def _clean(text: str | None) -> str:
    """Без управляющих символов (кроме перевода строки) и лишних пробелов по краям."""
    return "".join(ch for ch in (text or "") if ch == "\n" or ch.isprintable()).strip()


def send(db: DB, club: Club, staff: Account, pc_id: int, text: str | None, now: datetime) -> PcNotice:
    text = _clean(text)
    if not text:
        raise ServiceError(422, "Введите текст сообщения")
    if len(text) > MAX_LEN:
        raise ServiceError(422, f"Сообщение длиннее {MAX_LEN} знаков, сократите его")
    pc = db.scalar(select(PC).where(PC.id == pc_id, PC.club_id == club.id))
    if not pc:
        raise ServiceError(404, "ПК не найден")
    notice = PcNotice(club_id=club.id, pc_id=pc.id, text=text, created_by=staff.id, created_at=now)
    db.add(notice)
    db.flush()
    return notice


def deliver(db: DB, pc: PC, now: datetime) -> list[dict]:
    """Свежие сообщения для этого ПК (старые первыми). Первая выдача отмечается как «доставлено». Коммитит вызывающий."""
    rows = list(db.scalars(select(PcNotice).where(
        PcNotice.pc_id == pc.id, PcNotice.created_at > now - timedelta(seconds=FRESH_SECONDS))
        .order_by(PcNotice.id.desc()).limit(MAX_PER_STATE)))
    rows.reverse()
    for n in rows:
        if n.delivered_at is None:
            n.delivered_at = now
    return [{"id": n.id, "text": n.text} for n in rows]


def latest_by_pc(db: DB, club_id: int, now: datetime) -> dict[int, PcNotice]:
    """Последнее сообщение каждого ПК за последние минуты: админка показывает его в панели ПК и «показано на ПК»."""
    out: dict[int, PcNotice] = {}
    for n in db.scalars(select(PcNotice).where(
            PcNotice.club_id == club_id, PcNotice.created_at > now - timedelta(minutes=RECENT_MINUTES)).order_by(PcNotice.id)):
        out[n.pc_id] = n
    return out


def notice_out(n: PcNotice) -> dict:
    return {"id": n.id, "pc_id": n.pc_id, "text": n.text, "created_at": n.created_at, "delivered_at": n.delivered_at}
