"""Кто сейчас вошёл на ПК. Правила:
- на одном ПК один активный вход;
- сотрудник → ПК открывается как обычный рабочий стол (режим staff_desktop);
- гость → личный кабинет: ПК остаётся заблокирован, пока гость не стартует сессию;
- вход гостя заканчивается по бездействию (clubs.idle_logout_minutes), пока он не играет.
Этот модуль читает сессии напрямую из моделей, а не из services.sessions (чтобы не было циклического импорта)."""
from datetime import datetime, timedelta

from sqlalchemy import select, update
from sqlalchemy.orm import Session as DB

from .. import config
from ..errors import ServiceError
from ..models import Account, Club, Login, PC, Session
from ..security import hash_secret, new_secret


def _active_session_at_pc(db: DB, pc_id: int) -> Session | None:
    return db.scalar(select(Session).where(Session.pc_id == pc_id, Session.ended_at.is_(None)))


def _has_active_session(db: DB, account_id: int) -> bool:
    return db.scalar(select(Session.id).where(Session.account_id == account_id, Session.ended_at.is_(None))) is not None


def is_stale(db: DB, login: Login, account: Account, club: Club, now: datetime) -> bool:
    """Вход устарел: истёк срок или гость долго ничего не делает (и не играет)."""
    if login.ended_at is not None or login.expires_at <= now:
        return True
    if account.role == "guest" and now - login.last_seen_at > timedelta(minutes=club.idle_logout_minutes):
        return not _has_active_session(db, account.id)
    return False


def current_login(db: DB, club: Club, pc_id: int, now: datetime) -> tuple[Login, Account] | None:
    """Активный вход на ПК. Устаревший закрывает на месте (коммитит вызывающий)."""
    login = db.scalar(select(Login).where(Login.pc_id == pc_id, Login.ended_at.is_(None)))
    if not login:
        return None
    account = db.get(Account, login.account_id)
    if is_stale(db, login, account, club, now):
        login.ended_at = now
        return None
    return login, account


def guest_at_pc(db: DB, club: Club, pc_id: int, now: datetime) -> Account | None:
    """Гость, который сейчас сидит за ПК: тот, у кого идёт сессия, а если сессии нет, вошедший в кабинет. Никого нет, если ПК закрыт
    или на нём рабочий стол сотрудника. Устаревший вход закрывается на месте (коммитит вызывающий)."""
    session = _active_session_at_pc(db, pc_id)
    if session:
        return db.get(Account, session.account_id)
    current = current_login(db, club, pc_id, now)
    if current and current[1].role == "guest":
        return current[1]
    return None


def staff_desktop_pc_ids(db: DB, club_id: int, now: datetime) -> set[int]:
    rows = db.execute(select(Login.pc_id).join(Account, Account.id == Login.account_id).where(
        Login.club_id == club_id, Login.pc_id.is_not(None), Login.ended_at.is_(None),
        Login.expires_at > now, Account.role == "staff"))
    return {pc_id for (pc_id,) in rows}


def staff_login_active(db: DB, club: Club, pc_id: int, now: datetime) -> bool:
    cur = current_login(db, club, pc_id, now)
    return bool(cur and cur[1].role == "staff")


def check_can_login(db: DB, club: Club, pc: PC, role: str, account: Account | None, now: datetime) -> None:
    """Можно ли сейчас войти на этот ПК. account=None — гость ещё не создан (регистрация)."""
    cur = current_login(db, club, pc.id, now)
    sess = _active_session_at_pc(db, pc.id)
    if role == "guest":
        if pc.maintenance:
            raise ServiceError(409, "ПК на обслуживании. Обратитесь к администратору")
        if cur and cur[1].role == "staff":
            raise ServiceError(409, "На этом ПК выполнен вход сотрудника")
        if sess and (account is None or sess.account_id != account.id):
            raise ServiceError(409, "На этом ПК идёт сессия другого гостя")
    elif sess:
        raise ServiceError(409, "На ПК идёт гостевая сессия. Сначала завершите её")


def open_pc_login(db: DB, club: Club, pc: PC, account: Account, now: datetime) -> str:
    """Закрывает прежний вход на ПК (кабинет без игры / другой сотрудник) и выдаёт токен новому.
    Токен виден в открытом виде только здесь. Коммитит вызывающий."""
    check_can_login(db, club, pc, account.role, account, now)
    cur = current_login(db, club, pc.id, now)
    if cur:
        cur[0].ended_at = now
    db.flush()  # старый вход должен закрыться до вставки нового (уникальный индекс «один активный вход на ПК»)
    hours = config.STAFF_TOKEN_HOURS if account.role == "staff" else config.GUEST_TOKEN_HOURS
    token = new_secret()
    db.add(Login(club_id=club.id, account_id=account.id, pc_id=pc.id, token_hash=hash_secret(token),
                 created_at=now, last_seen_at=now, expires_at=now + timedelta(hours=hours)))
    return token


def end_account_logins(db: DB, account_id: int, now: datetime) -> None:
    db.execute(update(Login).where(Login.account_id == account_id, Login.ended_at.is_(None)).values(ended_at=now))


def end_pc_logins(db: DB, pc_id: int, now: datetime) -> None:
    db.execute(update(Login).where(Login.pc_id == pc_id, Login.ended_at.is_(None)).values(ended_at=now))
