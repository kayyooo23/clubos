"""Сессии гостей на ПК. Каждая функция принимает club (или club_id) и работает только в его пределах.
Оплата с баланса:
  package / night — вся цена списывается при старте; когда тариф кончился, а гость остался и у него есть деньги, сессия
                    продолжается по почасовой ставке клуба, а набежавшая доплата списывается при закрытии;
  hourly          — списывается при закрытии по фактическим минутам, а конец сессии = момент, когда кончатся деньги."""
from datetime import datetime, timedelta

from sqlalchemy import select, update
from sqlalchemy.exc import IntegrityError
from sqlalchemy.orm import Session as DB

from ..errors import ServiceError
from ..models import Account, Club, PC, Session, Tariff
from . import billing, bookings, logins, wallet, zones


def _finalize(db: DB, s: Session, end: datetime, now: datetime) -> bool:
    """Закрывает сессию на момент end и фиксирует стоимость. Для hourly здесь же списываются деньги.
    Сессию закрывает ровно один запрос: условное обновление «ended_at IS NULL» занимает её, и тот, кто опоздал (два нажатия «Остановить» одновременно,
    два запроса, каждый из которых вызвал close_expired), ничего не списывает. Возвращает False, если сессию уже закрыл другой запрос."""
    account = db.get(Account, s.account_id)
    wallet.lock(db, account)                           # сначала аккаунт, потом сессия (порядок блокировок, см. wallet.lock); баланс актуальный
    claimed = db.execute(update(Session).where(Session.id == s.id, Session.ended_at.is_(None)).values(ended_at=end)).rowcount
    if claimed != 1:
        db.refresh(s)                                  # подтягиваем то, что записал победитель
        return False
    if s.tariff.kind == "hourly":
        cost = min(billing.compute_cost(s, now), account.balance)  # на случай ручной корректировки баланса вниз
        if cost > 0:
            wallet.apply(db, account, -cost, "session_charge", now, session_id=s.id)
        s.cost = cost
    else:
        # цена тарифа уже списана при старте; здесь списывается только доплата по часам за время после его конца
        extra = min(billing.overtime_cost(s, now), account.balance)
        if extra > 0:
            wallet.apply(db, account, -extra, "session_charge", now, session_id=s.id,
                         comment=f"Доплата по часам после тарифа, {s.overtime_price} ₽/ч")
        s.cost = billing.session_price(s) + (s.extra_paid or 0) + extra
    return True


def close_expired(db: DB, club_id: int, now: datetime) -> None:
    """Закрывает сессии клуба, у которых вышло время (пакет/ночь) или кончились деньги (hourly)."""
    for s in db.scalars(select(Session).where(
            Session.club_id == club_id, Session.ended_at.is_(None), Session.planned_end.is_not(None)).order_by(Session.id)):   # порядок один и тот же у всех запросов
        if billing.is_expired(s, now):
            _finalize(db, s, s.planned_end, now)
    db.commit()


def active_session(db: DB, club_id: int, pc_id: int) -> Session | None:
    return db.scalar(select(Session).where(
        Session.club_id == club_id, Session.pc_id == pc_id, Session.ended_at.is_(None)))


def active_session_for_account(db: DB, club_id: int, account_id: int) -> Session | None:
    return db.scalar(select(Session).where(
        Session.club_id == club_id, Session.account_id == account_id, Session.ended_at.is_(None)))


def active_sessions_by_pc(db: DB, club_id: int) -> dict[int, Session]:
    return {s.pc_id: s for s in db.scalars(select(Session).where(
        Session.club_id == club_id, Session.ended_at.is_(None)))}


def session_out(s: Session, now: datetime) -> dict:
    """seconds_left: пока идёт оплаченный тариф, это время до его конца; когда тариф кончился и идёт доплата по часам
    (overtime = true), это время, на которое хватает баланса. overtime_price: ставка доплаты, ₽/час (пусто, если доплаты нет)."""
    if s.ended_at:
        left = None
    elif s.base_end and s.overtime_price and now < s.base_end:
        left = max(int((s.base_end - now).total_seconds()), 0)
    elif s.planned_end:
        left = max(int((s.planned_end - now).total_seconds()), 0)
    else:
        left = None
    return {
        "id": s.id, "pc_id": s.pc_id, "account_id": s.account_id, "tariff": s.tariff.name, "kind": s.tariff.kind,
        "started_at": s.started_at, "planned_end": s.planned_end, "ended_at": s.ended_at,
        "cost": s.cost if s.ended_at else billing.compute_cost(s, now),
        "seconds_left": left,
        "overtime": billing.in_overtime(s, now), "overtime_price": s.overtime_price,
    }


def get_pc(db: DB, club_id: int, pc_id: int) -> PC:
    pc = db.scalar(select(PC).where(PC.id == pc_id, PC.club_id == club_id))
    if not pc:
        raise ServiceError(404, "ПК не найден")
    return pc


def get_tariff(db: DB, club_id: int, tariff_id: int) -> Tariff:
    t = db.scalar(select(Tariff).where(Tariff.id == tariff_id, Tariff.club_id == club_id))
    if not t:
        raise ServiceError(404, "Тариф не найден")
    return t


def start_session(db: DB, club: Club, account: Account, pc_id: int, tariff_id: int, now: datetime) -> Session:
    """Старт сессии гостя account на ПК pc_id за счёт его баланса."""
    close_expired(db, club.id, now)
    if account.club_id != club.id:
        raise ServiceError(404, "Аккаунт не найден")
    wallet.lock(db, account)                         # сначала аккаунт (порядок блокировок, см. wallet.lock): баланс ниже читается актуальный
    if account.role != "guest" or account.status != "active":
        raise ServiceError(403, "Аккаунт не может играть")
    pc, t = get_pc(db, club.id, pc_id), get_tariff(db, club.id, tariff_id)
    if not t.active or not zones.tariff_applies(db, club.id, t, pc):
        raise ServiceError(404, "Тариф не найден")           # тарифа нет или он не из зала этого ПК: гость такого не видит
    if pc.maintenance:
        raise ServiceError(409, "ПК на обслуживании")
    if active_session(db, club.id, pc.id):
        raise ServiceError(409, "На этом ПК уже идёт сессия")
    if active_session_for_account(db, club.id, account.id):
        raise ServiceError(409, "У гостя уже идёт сессия")
    if logins.staff_login_active(db, club, pc.id, now):
        raise ServiceError(409, "На этом ПК выполнен вход сотрудника")
    if t.kind == "night" and not billing.in_night_window(t, now, club.utc_offset):
        raise ServiceError(400, "Ночной тариф сейчас не действует")
    price = billing.price_at(t, now, club.utc_offset)    # цена по дню недели на момент старта: на всю сессию, через полночь не меняется
    need = billing.min_hourly_balance(t, price) if t.kind == "hourly" else price
    if account.balance < need:
        raise ServiceError(402, f"Не хватает {need - account.balance} ₽ на балансе")
    booking = bookings.check_can_start(db, club, pc.id, account, now)   # чужая бронь держит ПК: отказ; своя: отметим использованной
    seated = logins.guest_at_pc(db, club, pc.id, now)    # сессии на ПК нет (проверено выше), значит это гость в кабинете
    if seated and seated.id != account.id:
        if booking is None:
            # иначе тот, кто сел за ПК, получил бы чужое оплаченное время
            raise ServiceError(409, "За этим ПК сидит другой гость. Отправьте его за другой ПК («Заблокировать ПК») и запустите сессию снова")
        logins.end_pc_logins(db, pc.id, now)               # у забронировавшего приоритет над чужим гостем в кабинете

    planned_end = billing.compute_planned_end(t, now, club.utc_offset, account.balance, price)
    base_end = rate = None
    if t.kind != "hourly":
        base_end = billing.base_end_time(t, now, club.utc_offset)
        rate = overtime_rate(db, club.id, pc.zone_id, now, club.utc_offset)  # ставка фиксируется на старте: правка тарифов посреди игры её не меняет
        if rate:
            planned_end = billing.overtime_limit_end(base_end, account.balance - price, rate)
    s = Session(club_id=club.id, pc_id=pc.id, tariff_id=t.id, account_id=account.id, started_at=now, price=price,
                planned_end=planned_end, base_end=base_end if rate else None, overtime_price=rate)
    try:
        db.add(s)
        db.flush()
        if t.kind != "hourly":
            wallet.apply(db, account, -price, "session_charge", now, session_id=s.id)
        if booking:
            booking.status = "used"
        db.commit()
    except IntegrityError:
        db.rollback()
        raise ServiceError(409, "ПК или гость уже заняты другой сессией")
    except ServiceError:
        db.rollback()
        raise
    return s


def extend_session(db: DB, club: Club, account: Account, tariff_id: int, now: datetime) -> Session:
    """Гость покупает ещё один пакет посреди игры: время добавляется к оплаченному, цена списывается с баланса сразу.
    Если оплаченный тариф уже кончился и шла доплата по часам, набежавшая доплата закрывается перед продлением, а новый пакет
    считается от этого момента (иначе одни и те же минуты оплачивались бы дважды). Продлевать можно сессии, начатые с пакета или ночи;
    у почасовой сессии оплаченного времени нет, её продлевает пополнение баланса."""
    close_expired(db, club.id, now)
    wallet.lock(db, account)                         # сначала аккаунт, потом сессия (порядок блокировок, см. wallet.lock)
    s = active_session_for_account(db, club.id, account.id)
    if not s:
        raise ServiceError(404, "У вас нет активной сессии")
    t = get_tariff(db, club.id, tariff_id)
    if not t.active or not zones.tariff_applies(db, club.id, t, db.get(PC, s.pc_id)):
        raise ServiceError(404, "Тариф не найден")
    if t.kind != "package":
        raise ServiceError(400, "Продлить можно только пакетом часов")
    if s.tariff.kind == "hourly":
        raise ServiceError(409, "Почасовую сессию продлевает пополнение баланса: играйте, пока хватает денег")

    overtime_mode = bool(s.base_end and s.overtime_price)
    in_over = billing.in_overtime(s, now)
    accrued = billing.overtime_cost(s, now) if in_over else 0
    price = billing.price_at(t, now, club.utc_offset)    # продление стоит по дню, в который куплено
    need = accrued + price
    if account.balance < need:
        raise ServiceError(402, f"Не хватает {need - account.balance} ₽ на балансе")

    # новый конец оплаченного времени: к старому, а если он уже прошёл, то от текущего момента
    if overtime_mode:
        new_paid_end = (now if in_over else s.base_end) + timedelta(minutes=t.package_minutes)
    else:
        new_paid_end = s.planned_end + timedelta(minutes=t.package_minutes)
    bookings.check_can_extend(db, club, s.pc_id, account, new_paid_end, now)

    try:
        if accrued > 0:
            wallet.apply(db, account, -accrued, "session_charge", now, session_id=s.id,
                         comment=f"Доплата по часам после тарифа, {s.overtime_price} ₽/ч (перед продлением)")
        wallet.apply(db, account, -price, "session_charge", now, session_id=s.id, comment=f"Продление: {t.name}")
        s.extra_paid = (s.extra_paid or 0) + accrued + price
        if overtime_mode:
            s.base_end = new_paid_end
            s.planned_end = billing.overtime_limit_end(s.base_end, account.balance, s.overtime_price)
        else:
            s.planned_end = new_paid_end
        db.commit()
    except ServiceError:
        db.rollback()
        raise
    return s


def stop_session(db: DB, club_id: int, session_id: int, now: datetime, *, account_id: int | None = None) -> Session:
    """Остановка сессии. account_id задан — гость может остановить только свою."""
    close_expired(db, club_id, now)
    q = select(Session).where(Session.id == session_id, Session.club_id == club_id)
    if account_id is not None:
        q = q.where(Session.account_id == account_id)
    s = db.scalar(q)
    if not s:
        raise ServiceError(404, "Сессия не найдена")
    if s.ended_at is None:
        _finalize(db, s, now, now)
        db.commit()
    return s


def overtime_rate(db: DB, club_id: int, zone_id: int | None = None, now: datetime | None = None, utc_offset: int = 0) -> int | None:
    """Почасовая ставка для доплаты после конца пакета или ночи: цена самого дешёвого действующего почасового тарифа среди тех, что видит ПК этого зала
    (у VIP своя ставка, а не общая), по дню недели на момент now. Если почасового тарифа нет, доплаты нет и сессия кончается вместе с тарифом."""
    prices = [billing.price_at(t, now, utc_offset) if now else t.price
              for t in zones.applicable_tariffs(db, club_id, zone_id) if t.kind == "hourly"]
    return min(prices) if prices else None


def recalc_limit(db: DB, account: Account, now: datetime) -> None:
    """Баланс изменился (пополнение/корректировка): пересчитываем самый поздний конец идущей сессии, у которой он зависит
    от баланса: почасовой или в доплате по часам после пакета. Коммитит вызывающий."""
    s = active_session_for_account(db, account.club_id, account.id)
    if not s:
        return
    if s.tariff.kind == "hourly":
        s.planned_end = billing.hourly_limit_end(s.tariff, s.started_at, account.balance, billing.session_price(s))
    elif s.base_end and s.overtime_price:
        s.planned_end = billing.overtime_limit_end(s.base_end, account.balance, s.overtime_price)
