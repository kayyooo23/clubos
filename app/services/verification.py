"""Коды подтверждения телефона. Каждая SMS стоит денег, поэтому лимиты жёсткие.
Ответы одинаковы для известных и неизвестных номеров, чтобы по ним нельзя было узнать, кто зарегистрирован."""
import hashlib
import hmac
import secrets
from datetime import datetime, timedelta

from sqlalchemy import func, select, update
from sqlalchemy.orm import Session as DB

from .. import config
from ..errors import ServiceError
from ..models import Club, PC, PhoneVerification
from .sms import SmsError, SmsProvider

BAD_CODE = "Неверный или просроченный код"


def _hash(club_id: int, phone: str, purpose: str, code: str) -> str:
    return hashlib.sha256(f"{club_id}:{phone}:{purpose}:{code}".encode()).hexdigest()


def _count(db: DB, since: datetime, **filters) -> int:
    q = select(func.count(PhoneVerification.id)).where(PhoneVerification.created_at >= since)
    for col, val in filters.items():
        q = q.where(getattr(PhoneVerification, col) == val)
    return db.scalar(q)


def request_code(db: DB, sms: SmsProvider, club: Club, pc: PC, phone: str, purpose: str, *,
                 deliver: bool, ip: str | None, now: datetime) -> int:
    """Создаёт код и (если deliver) отправляет SMS. Возвращает, через сколько секунд можно запросить новый.
    deliver=False — «номер нам не подходит» (уже зарегистрирован / нет аккаунта): запись и лимиты те же, SMS не уходит."""
    last = db.scalar(select(PhoneVerification).where(
        PhoneVerification.club_id == club.id, PhoneVerification.phone == phone,
        PhoneVerification.purpose == purpose).order_by(PhoneVerification.id.desc()).limit(1))
    if last:
        wait = config.SMS_RESEND_SECONDS - (now - last.created_at).total_seconds()
        if wait > 0:
            raise ServiceError(429, f"Повторите запрос через {int(wait) + 1} с")
    hour_ago, day_ago = now - timedelta(hours=1), now - timedelta(days=1)
    if (_count(db, hour_ago, club_id=club.id, phone=phone) >= config.SMS_PER_PHONE_PER_HOUR
            or _count(db, hour_ago, pc_id=pc.id) >= config.SMS_PER_PC_PER_HOUR):
        raise ServiceError(429, "Слишком много запросов кода. Повторите позже")
    if _count(db, day_ago, club_id=club.id) >= config.SMS_PER_CLUB_PER_DAY:
        raise ServiceError(429, "Лимит SMS на сегодня исчерпан. Обратитесь к администратору")

    # новый код отменяет предыдущие: действителен только последний
    db.execute(update(PhoneVerification).where(
        PhoneVerification.club_id == club.id, PhoneVerification.phone == phone,
        PhoneVerification.purpose == purpose, PhoneVerification.consumed_at.is_(None)).values(consumed_at=now))
    code = f"{secrets.randbelow(10 ** 6):06d}"
    db.add(PhoneVerification(club_id=club.id, phone=phone, purpose=purpose, code_hash=_hash(club.id, phone, purpose, code),
                             expires_at=now + timedelta(minutes=config.VERIFY_CODE_TTL_MINUTES), created_at=now,
                             ip=ip, pc_id=pc.id))
    db.commit()
    if deliver:
        try:
            sms.send(phone, f"Код подтверждения ClubOS: {code}. Никому не сообщайте его.")
        except SmsError:
            raise ServiceError(502, "Не удалось отправить SMS. Попробуйте позже")
    return config.SMS_RESEND_SECONDS


def consume_code(db: DB, club_id: int, phone: str, purpose: str, code: str, now: datetime) -> None:
    """Проверяет код. После VERIFY_MAX_ATTEMPTS неверных попыток код сгорает. Успешный код одноразовый."""
    row = db.scalar(select(PhoneVerification).where(
        PhoneVerification.club_id == club_id, PhoneVerification.phone == phone,
        PhoneVerification.purpose == purpose, PhoneVerification.consumed_at.is_(None),
        PhoneVerification.expires_at > now, PhoneVerification.attempts < config.VERIFY_MAX_ATTEMPTS
    ).order_by(PhoneVerification.id.desc()).limit(1))
    if not row:
        raise ServiceError(400, BAD_CODE)
    row.attempts += 1
    ok = hmac.compare_digest(row.code_hash, _hash(club_id, phone, purpose, code))
    if ok or row.attempts >= config.VERIFY_MAX_ATTEMPTS:
        row.consumed_at = now
    db.commit()  # попытка засчитывается в любом случае
    if not ok:
        raise ServiceError(400, BAD_CODE)
