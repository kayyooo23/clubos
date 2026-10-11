"""Операции сотрудника с балансом гостя: пополнение наличными, ручная корректировка, блокировка."""
from datetime import datetime

from sqlalchemy.orm import Session as DB

from .. import config
from ..errors import ServiceError
from ..models import Account, Club
from . import audit, logins, sessions, wallet


def change_balance(db: DB, club: Club, staff: Account, account: Account, amount: int, kind: str,
                   comment: str | None, now: datetime, ip: str | None = None) -> None:
    """kind: topup_cash (amount > 0) или adjustment (± с обязательным комментарием). Атомарно: баланс, журнал, аудит."""
    if abs(amount) > config.MAX_TOPUP:
        raise ServiceError(422, f"Сумма не может быть больше {config.MAX_TOPUP} ₽")
    # сначала закрываем просроченное: иначе пополнение «воскресило» бы сессию, которая уже должна была закончиться
    sessions.close_expired(db, club.id, now)
    db.refresh(account)
    wallet.apply(db, account, amount, kind, now, staff_id=staff.id, comment=comment)
    sessions.recalc_limit(db, account, now)
    audit.record(db, club.id, kind, now, actor=staff, details=f"account={account.id} amount={amount}", ip=ip)
    db.commit()


def set_blocked(db: DB, club: Club, staff: Account, account: Account, blocked: bool, now: datetime,
                ip: str | None = None) -> None:
    """Блокировка гостя: сразу завершает его входы и идущую сессию (деньги за сессию списываются как обычно)."""
    account.status = "blocked" if blocked else "active"
    if blocked:
        s = sessions.active_session_for_account(db, club.id, account.id)
        if s:
            sessions.stop_session(db, club.id, s.id, now)
        logins.end_account_logins(db, account.id, now)
    audit.record(db, club.id, "account_block" if blocked else "account_unblock", now, actor=staff,
                 details=f"account={account.id}", ip=ip)
    db.commit()
