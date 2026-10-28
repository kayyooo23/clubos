"""Деньги гостей. ЕДИНСТВЕННОЕ место, которое меняет accounts.balance.
Каждое изменение — запись в журнале wallet_transactions в той же транзакции; баланс не может уйти в минус."""
from datetime import datetime

from sqlalchemy import update
from sqlalchemy.orm import Session as DB

from ..errors import ServiceError
from ..models import Account, WalletTransaction


def lock(db: DB, account: Account) -> None:
    """Блокирует строку гостя до конца транзакции и подтягивает актуальный баланс (SELECT ... FOR NO KEY UPDATE на PostgreSQL; на SQLite ничего:
    там запись и так идёт по очереди). Порядок блокировок во всём коде один: сначала аккаунт, потом сессия (и всё остальное). Если брать их
    вразнобой, два запроса, один из которых пополняет баланс, а другой останавливает сессию, блокируют друг друга (DeadlockDetected).
    NO KEY UPDATE, а не UPDATE: ключ аккаунта мы не меняем, и запись журнала со ссылкой на аккаунт не ждёт эту блокировку."""
    db.refresh(account, with_for_update={"key_share": True})


def apply(db: DB, account: Account, amount: int, kind: str, now: datetime, *, session_id: int | None = None,
          staff_id: int | None = None, comment: str | None = None) -> WalletTransaction:
    """Меняет баланс на amount (± рубли) и пишет журнал. НЕ коммитит: решает вызывающий,
    чтобы списание и старт сессии были одной транзакцией."""
    if amount == 0:
        raise ValueError("amount must not be zero")
    # условие balance + amount >= 0 проверяется самой БД атомарно, поэтому два одновременных списания не уведут баланс в минус
    res = db.execute(update(Account)
                     .where(Account.id == account.id, Account.club_id == account.club_id,
                            Account.balance + amount >= 0)
                     .values(balance=Account.balance + amount))
    if res.rowcount != 1:
        raise ServiceError(402, "Недостаточно средств на балансе")
    db.refresh(account)  # подтягиваем новый баланс в объект
    tx = WalletTransaction(club_id=account.club_id, account_id=account.id, amount=amount,
                           balance_after=account.balance, kind=kind, session_id=session_id,
                           staff_id=staff_id, comment=comment, created_at=now)
    db.add(tx)
    db.flush()
    return tx
