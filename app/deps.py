"""«Контекст клуба»: из ключа ПК или токена сервер сам определяет, чей это запрос.
club_id никогда не берётся из тела запроса или URL — иначе гость одного клуба подставил бы чужой.

Три вида доступа, друг на друга не заменяются:
  X-Platform-Key          — владелец платформы;
  X-PC-Key                — «я ПК №N такого-то клуба» (heartbeat, регистрация, вход);
  Bearer-токен + X-PC-Key — гость или сотрудник, вошедший на этом ПК (токен выдан именно для него);
  Bearer-токен            — сотрудник, вошедший в админку (токен выдан без привязки к ПК)."""
import hmac
from dataclasses import dataclass
from datetime import timedelta

from fastapi import Depends, Security
from fastapi.security import APIKeyHeader, HTTPAuthorizationCredentials, HTTPBearer
from sqlalchemy import select
from sqlalchemy.orm import Session as DB

from . import config
from .db import get_db
from .errors import ServiceError
from .models import Account, Club, Login, PC
from .security import hash_secret
from .services import logins as login_rules
from .timeutil import utcnow

platform_key_header = APIKeyHeader(name="X-Platform-Key", auto_error=False)
pc_key_header = APIKeyHeader(name="X-PC-Key", auto_error=False)
bearer = HTTPBearer(auto_error=False)  # в /docs: кнопка Authorize -> вставить токен

BAD_LOGIN = "Вход недействителен, войдите заново"


@dataclass
class PCContext:
    club: Club
    pc: PC


@dataclass
class PCLoginContext:
    """Тот, кто вошёл на этом ПК: гость или сотрудник."""
    club: Club
    pc: PC
    account: Account
    login: Login


@dataclass
class GuestContext(PCLoginContext):
    pass


@dataclass
class StaffContext:
    club: Club
    account: Account
    login: Login


def platform_only(key: str | None = Security(platform_key_header)) -> None:
    if not key or not hmac.compare_digest(key.encode(), config.PLATFORM_KEY.encode()):
        raise ServiceError(401, "Неверный ключ платформы")


def pc_context(key: str | None = Security(pc_key_header), db: DB = Depends(get_db)) -> PCContext:
    if not key:
        raise ServiceError(401, "Нужен ключ ПК (заголовок X-PC-Key)")
    pc = db.scalar(select(PC).where(PC.key_hash == hash_secret(key)))
    if not pc:
        raise ServiceError(401, "Неверный ключ ПК")
    return PCContext(club=db.get(Club, pc.club_id), pc=pc)


def _find_login(db: DB, creds: HTTPAuthorizationCredentials | None, now) -> Login:
    if not creds:
        raise ServiceError(401, "Нужен вход")
    login = db.scalar(select(Login).where(Login.token_hash == hash_secret(creds.credentials)))
    if not login or login.ended_at is not None or login.expires_at <= now:
        raise ServiceError(401, BAD_LOGIN)
    return login


def _touch(db: DB, login: Login, now) -> None:
    if now - login.last_seen_at > timedelta(seconds=60):  # не пишем в БД на каждый запрос
        login.last_seen_at = now
        db.commit()


def pc_login_context(creds: HTTPAuthorizationCredentials | None = Security(bearer),
                     ctx: PCContext = Depends(pc_context), db: DB = Depends(get_db)) -> PCLoginContext:
    now = utcnow()
    login = _find_login(db, creds, now)
    if login.pc_id != ctx.pc.id:  # токен выдан для другого ПК (или для админки) — здесь не работает
        raise ServiceError(401, BAD_LOGIN)
    account = db.get(Account, login.account_id)
    if account.status != "active":
        raise ServiceError(403, "Аккаунт заблокирован")
    if ctx.club.status != "active":
        raise ServiceError(403, "Клуб приостановлен")
    if login_rules.is_stale(db, login, account, ctx.club, now):  # гость долго бездействовал
        login.ended_at = now
        db.commit()
        raise ServiceError(401, BAD_LOGIN)
    _touch(db, login, now)
    return PCLoginContext(club=ctx.club, pc=ctx.pc, account=account, login=login)


def guest_context(ctx: PCLoginContext = Depends(pc_login_context)) -> GuestContext:
    if ctx.account.role != "guest":
        raise ServiceError(403, "Недостаточно прав")
    return GuestContext(club=ctx.club, pc=ctx.pc, account=ctx.account, login=ctx.login)


def staff_context(creds: HTTPAuthorizationCredentials | None = Security(bearer),
                  db: DB = Depends(get_db)) -> StaffContext:
    """Сотрудник, вошедший в админку. Токен, выданный на ПК (рабочий стол), для админки не годится:
    ПК — недоверенная среда, и токен оттуда не должен открывать управление клубом."""
    now = utcnow()
    login = _find_login(db, creds, now)
    if login.pc_id is not None:
        raise ServiceError(403, "Этот вход выдан для ПК, а не для админки")
    account, club = db.get(Account, login.account_id), db.get(Club, login.club_id)
    if account.role != "staff" or account.status != "active":
        raise ServiceError(403, "Недостаточно прав")
    if club.status != "active":
        raise ServiceError(403, "Клуб приостановлен")
    _touch(db, login, now)
    return StaffContext(club=club, account=account, login=login)
