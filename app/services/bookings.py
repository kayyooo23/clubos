"""Бронирование ПК. Всё строго в пределах клуба.
Бронь держит ПК за гостем (по номеру телефона) в окне: от «за LEAD минут до начала» до «GRACE минут после начала»
(но не дольше конца брони). В это окно сессию на ПК может начать только тот, на чей номер бронь; пришёл и начал, бронь
становится used. Не пришёл за GRACE минут, ПК освобождается сам: бронь остаётся в списке, но ничего не держит."""
from datetime import datetime, timedelta, timezone

from sqlalchemy import select
from sqlalchemy.orm import Session as DB

from .. import config
from ..errors import ServiceError
from ..models import Account, Booking, Club, PC, Session
from ..schemas import BookingIn, BookingPatch
from . import logins

MAX_DAYS_AHEAD = 30
LATE_CREATE_SECONDS = 5 * 60       # бронь «на сейчас» вводят с небольшим опозданием: пять минут в прошлое допускаются


def hold_window(b: Booking) -> tuple[datetime, datetime]:
    """Когда бронь реально держит ПК."""
    return (b.starts_at - timedelta(minutes=config.BOOKING_LEAD_MINUTES),
            min(b.ends_at, b.starts_at + timedelta(minutes=config.BOOKING_GRACE_MINUTES)))


def is_holding(b: Booking, now: datetime) -> bool:
    start, end = hold_window(b)
    return b.status == "active" and start <= now <= end


def booking_out(b: Booking, now: datetime, *, pc_name: str | None = None) -> dict:
    out = {"id": b.id, "pc_id": b.pc_id, "phone": b.phone, "name": b.name, "comment": b.comment,
           "starts_at": b.starts_at, "ends_at": b.ends_at, "status": b.status, "holding": is_holding(b, now),
           "missed": b.status == "active" and now > hold_window(b)[1]}
    if pc_name is not None:
        out["pc_name"] = pc_name
    return out


def _naive_utc(dt: datetime) -> datetime:
    return dt.astimezone(timezone.utc).replace(tzinfo=None) if dt.tzinfo else dt


def check_pc_not_occupied(db: DB, club: Club, pc: PC, start: datetime, phone: str, now: datetime) -> None:
    """Бронь не должна отбирать ПК у того, кто за ним сидит прямо сейчас (администратор у стойки или, позже, гость с телефона:
    один и тот же ПК не должен достаться двоим). Бронь держит ПК с «за LEAD минут до начала», поэтому смотрим на начало удержания.
    - идёт сессия с оплаченным концом (пакет, ночь): ПК занят до этого конца; у почасовой сессии конца нет (играют, пока хватает денег),
      она мешает только брони, которая уже держит ПК;
    - гость вошёл в кабинет или за ПК сидит сотрудник: мешают брони, которая уже держит ПК;
    - сидящий гость с тем же номером, что у брони, не мешает: это его собственная бронь."""
    hold_start = start - timedelta(minutes=config.BOOKING_LEAD_MINUTES)
    session = db.scalar(select(Session).where(Session.pc_id == pc.id, Session.ended_at.is_(None)))
    if session:
        if db.get(Account, session.account_id).phone == phone:
            return
        if session.base_end and session.overtime_price:
            paid_end = session.base_end
        else:
            paid_end = session.planned_end if session.tariff.kind != "hourly" else None
        if hold_start <= now or (paid_end is not None and hold_start < paid_end):
            if paid_end is not None and paid_end > now:
                local_end = paid_end + timedelta(hours=club.utc_offset)
                local_free = paid_end + timedelta(minutes=config.BOOKING_LEAD_MINUTES) + timedelta(hours=club.utc_offset)
                raise ServiceError(409, f"ПК занят: на нём идёт сессия до {local_end:%H:%M}. "
                                        f"Бронь возможна с {local_free:%H:%M} или на другом ПК")
            raise ServiceError(409, "ПК занят: на нём идёт сессия гостя. Выберите другой ПК или время позже")
        return
    if hold_start > now:
        return
    if logins.staff_login_active(db, club, pc.id, now):
        raise ServiceError(409, "ПК занят: на нём вошёл сотрудник. Выберите другой ПК или время позже")
    seated = logins.guest_at_pc(db, club, pc.id, now)
    if seated and seated.phone != phone:
        raise ServiceError(409, "ПК занят: за ним уже сидит гость. Выберите другой ПК или время позже")


def create(db: DB, club: Club, staff: Account, data: BookingIn, now: datetime) -> Booking:
    pc = db.scalar(select(PC).where(PC.id == data.pc_id, PC.club_id == club.id).with_for_update())   # на PostgreSQL две брони одного ПК встают в очередь
    if not pc:
        raise ServiceError(404, "ПК не найден")
    start = _naive_utc(data.starts_at)
    end = start + timedelta(minutes=data.minutes)
    if start < now - timedelta(seconds=LATE_CREATE_SECONDS):
        raise ServiceError(422, "Время начала уже прошло")
    if start > now + timedelta(days=MAX_DAYS_AHEAD):
        raise ServiceError(422, f"Бронь можно сделать не дальше, чем на {MAX_DAYS_AHEAD} дней вперёд")
    if pc.maintenance:
        raise ServiceError(409, "ПК на обслуживании, бронировать его нельзя")
    check_pc_not_occupied(db, club, pc, start, data.phone, now)
    clash = db.scalar(select(Booking).where(
        Booking.club_id == club.id, Booking.pc_id == pc.id, Booking.status == "active",
        Booking.starts_at < end, Booking.ends_at > start))
    if clash:
        raise ServiceError(409, "На это время ПК уже забронирован")
    b = Booking(club_id=club.id, pc_id=pc.id, phone=data.phone, name=data.name, comment=data.comment,
                starts_at=start, ends_at=end, status="active", created_by=staff.id, created_at=now)
    db.add(b)
    db.flush()
    return b


def update(db: DB, club: Club, booking_id: int, data: BookingPatch, now: datetime) -> tuple[Booking, list[str]]:
    """Правка действующей брони: гость позвонил и не успевает (сдвинуть время), просит другую мышку (комментарий), другой ПК, другой номер.
    Возвращает бронь и названия изменённых полей (для журнала). Закрытые брони (гость пришёл, отменена) не меняются."""
    b = db.scalar(select(Booking).where(Booking.id == booking_id, Booking.club_id == club.id))
    if not b:
        raise ServiceError(404, "Бронь не найдена")
    if b.status != "active":
        raise ServiceError(409, "Бронь уже закрыта: изменить её нельзя")
    given = data.model_fields_set
    changed: list[str] = []

    pc_id = data.pc_id if "pc_id" in given and data.pc_id is not None else b.pc_id
    if pc_id != b.pc_id:
        pc = db.scalar(select(PC).where(PC.id == pc_id, PC.club_id == club.id))
        if not pc:
            raise ServiceError(404, "ПК не найден")
        if pc.maintenance:
            raise ServiceError(409, "ПК на обслуживании, бронировать его нельзя")
        changed.append("pc")
    start = _naive_utc(data.starts_at) if "starts_at" in given and data.starts_at is not None else b.starts_at
    minutes = data.minutes if "minutes" in given and data.minutes is not None else int((b.ends_at - b.starts_at).total_seconds() // 60)
    end = start + timedelta(minutes=minutes)

    if start != b.starts_at:
        if start < now - timedelta(seconds=LATE_CREATE_SECONDS):
            raise ServiceError(422, "Время начала уже прошло")
        if start > now + timedelta(days=MAX_DAYS_AHEAD):
            raise ServiceError(422, f"Бронь можно сделать не дальше, чем на {MAX_DAYS_AHEAD} дней вперёд")
        changed.append("time")
    elif end != b.ends_at:
        changed.append("length")
    if pc_id != b.pc_id or start != b.starts_at:
        target = db.scalar(select(PC).where(PC.id == pc_id, PC.club_id == club.id).with_for_update())
        new_phone = data.phone if "phone" in given and data.phone is not None else b.phone
        check_pc_not_occupied(db, club, target, start, new_phone, now)    # новое место или время не должно отбирать ПК у сидящего за ним
    if pc_id != b.pc_id or start != b.starts_at or end != b.ends_at:
        clash = db.scalar(select(Booking).where(
            Booking.club_id == club.id, Booking.pc_id == pc_id, Booking.status == "active", Booking.id != b.id,
            Booking.starts_at < end, Booking.ends_at > start))
        if clash:
            raise ServiceError(409, "На это время ПК уже забронирован")
        b.pc_id, b.starts_at, b.ends_at = pc_id, start, end

    if "phone" in given and data.phone is not None and data.phone != b.phone:
        b.phone = data.phone
        changed.append("phone")
    if "name" in given and (data.name or None) != b.name:
        b.name = data.name or None
        changed.append("name")
    if "comment" in given and (data.comment or None) != b.comment:
        b.comment = data.comment or None
        changed.append("comment")
    db.flush()
    return b, changed


def cancel(db: DB, club_id: int, booking_id: int) -> Booking:
    b = db.scalar(select(Booking).where(Booking.id == booking_id, Booking.club_id == club_id))
    if not b:
        raise ServiceError(404, "Бронь не найдена")
    if b.status != "active":
        raise ServiceError(409, "Бронь уже закрыта")
    b.status = "cancelled"
    return b


def list_bookings(db: DB, club_id: int, now: datetime, *, upcoming_only: bool, limit: int = 200) -> list[dict]:
    q = (select(Booking, PC.name).join(PC, PC.id == Booking.pc_id).where(Booking.club_id == club_id))
    if upcoming_only:
        q = q.where(Booking.status == "active", Booking.ends_at > now).order_by(Booking.starts_at)
    else:
        q = q.order_by(Booking.starts_at.desc())
    return [booking_out(b, now, pc_name=name) for b, name in db.execute(q.limit(min(max(limit, 1), 500)))]


def holding_at(db: DB, club_id: int, pc_id: int, now: datetime) -> Booking | None:
    """Бронь, которая сейчас держит этот ПК."""
    soon = now + timedelta(minutes=config.BOOKING_LEAD_MINUTES)
    for b in db.scalars(select(Booking).where(
            Booking.club_id == club_id, Booking.pc_id == pc_id, Booking.status == "active",
            Booking.starts_at <= soon, Booking.ends_at > now).order_by(Booking.starts_at)):
        if is_holding(b, now):
            return b
    return None


def check_can_start(db: DB, club: Club, pc_id: int, account: Account, now: datetime) -> Booking | None:
    """Вызывается при старте сессии. Если ПК держит чужая бронь, отказ. Если своя, возвращает её, чтобы отметить used."""
    b = holding_at(db, club.id, pc_id, now)
    if b is None:
        return None
    if b.phone != account.phone:
        local = b.starts_at + timedelta(hours=club.utc_offset)
        raise ServiceError(409, f"ПК забронирован на {local:%H:%M}. Выберите другой ПК")
    return b


def check_can_extend(db: DB, club: Club, pc_id: int, account: Account, new_end: datetime, now: datetime) -> None:
    """Продление игры не должно залезать на чужую бронь: ПК держится за забронировавшим с «за LEAD минут до начала»."""
    lead = timedelta(minutes=config.BOOKING_LEAD_MINUTES)
    for b in db.scalars(select(Booking).where(
            Booking.club_id == club.id, Booking.pc_id == pc_id, Booking.status == "active",
            Booking.ends_at > now, Booking.starts_at < new_end + lead).order_by(Booking.starts_at)):
        if b.phone == account.phone or now > hold_window(b)[1]:
            continue           # своя бронь или та, на которую никто не пришёл
        local = b.starts_at + timedelta(hours=club.utc_offset)
        raise ServiceError(409, f"ПК забронирован на {local:%H:%M}. Продлить время на этом ПК нельзя")


def next_for_each_pc(db: DB, club_id: int, now: datetime) -> dict[int, Booking]:
    """Ближайшая действующая бронь у каждого ПК (для карты): та, что держит ПК сейчас, или та, что начнётся раньше всех."""
    result: dict[int, Booking] = {}
    for b in db.scalars(select(Booking).where(Booking.club_id == club_id, Booking.status == "active", Booking.ends_at > now)
                        .order_by(Booking.starts_at)):
        if b.pc_id not in result and (is_holding(b, now) or now <= b.starts_at):
            result[b.pc_id] = b
    return result
