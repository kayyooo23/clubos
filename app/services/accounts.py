"""Аккаунты и вход. Токен входа показывается один раз при выдаче, в БД лежит только его хеш."""
from datetime import datetime, timedelta

from sqlalchemy import select
from sqlalchemy.orm import Session as DB

from .. import config
from ..errors import ServiceError, commit_or_409
from ..models import Account, Login
from ..security import burn_password_check, hash_password, hash_secret, new_secret, verify_password
from . import logins

BAD_CREDENTIALS = "Неверный номер или пароль"  # одно и то же сообщение для всех причин отказа


def get_by_phone(db: DB, club_id: int, phone: str) -> Account | None:
    return db.scalar(select(Account).where(Account.club_id == club_id, Account.phone == phone))


def get_account(db: DB, club_id: int, account_id: int) -> Account:
    acc = db.scalar(select(Account).where(Account.id == account_id, Account.club_id == club_id))
    if not acc:
        raise ServiceError(404, "Аккаунт не найден")
    return acc


def create_account(db: DB, club_id: int, phone: str, password: str, role: str, now: datetime,
                   consent: bool = False) -> Account:
    """Телефон считается подтверждённым: вызывающий уже проверил SMS-код (или это сотрудник от платформы)."""
    acc = Account(club_id=club_id, phone=phone, password_hash=hash_password(password), role=role,
                  phone_verified_at=now, consent_at=now if consent else None)
    db.add(acc)
    commit_or_409(db, "Аккаунт с таким номером уже есть")
    return acc


def set_password(db: DB, acc: Account, new_password: str, now: datetime) -> None:
    """Смена пароля: снимает блокировку входа и завершает все текущие входы (старые токены перестают работать)."""
    acc.password_hash = hash_password(new_password)
    acc.failed_logins, acc.locked_until = 0, None
    logins.end_account_logins(db, acc.id, now)
    db.commit()


def authenticate(db: DB, club_id: int, phone: str, password: str, now: datetime) -> Account:
    """Проверяет номер+пароль внутри клуба. 5 неудач подряд — блокировка на 15 минут."""
    acc = db.scalar(select(Account).where(Account.club_id == club_id, Account.phone == phone))
    if not acc:
        burn_password_check(password)
        raise ServiceError(401, BAD_CREDENTIALS)
    if acc.locked_until and acc.locked_until > now:
        raise ServiceError(429, "Слишком много неудачных попыток. Повторите позже")
    password_ok = verify_password(password, acc.password_hash)  # всегда проверяем: время ответа не выдаёт статус
    if acc.status != "active" or not password_ok:
        if acc.status == "active":
            acc.failed_logins += 1
            if acc.failed_logins >= config.MAX_FAILED_LOGINS:
                acc.locked_until = now + timedelta(minutes=config.LOGIN_LOCK_MINUTES)
                acc.failed_logins = 0
            db.commit()
        raise ServiceError(401, BAD_CREDENTIALS)
    acc.failed_logins, acc.locked_until = 0, None
    db.commit()
    return acc


def open_login(db: DB, acc: Account, pc_id: int | None, now: datetime, ttl: timedelta) -> str:
    """Создаёт вход и возвращает токен (единственный раз, когда он виден в открытом виде)."""
    token = new_secret()
    db.add(Login(club_id=acc.club_id, account_id=acc.id, pc_id=pc_id, token_hash=hash_secret(token),
                 created_at=now, last_seen_at=now, expires_at=now + ttl))
    db.commit()
    return token
