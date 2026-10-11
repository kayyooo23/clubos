"""Кабинет гостя: баланс, история, старт и остановка своей сессии. Нужен токен гостя И ключ ПК (токен привязан к ПК)."""
from fastapi import APIRouter, Depends
from sqlalchemy import select
from sqlalchemy.orm import Session as DB

from ..db import get_db
from ..deps import GuestContext, guest_context
from ..errors import ServiceError
from ..models import WalletTransaction
from ..schemas import CallIn, GuestSessionIn, OrderIn
from ..services import sessions, shop
from ..timeutil import utcnow
from .auth import account_out

router = APIRouter(prefix="/api/me", tags=["кабинет гостя"])


@router.get("")
def me(ctx: GuestContext = Depends(guest_context)):
    return account_out(ctx.account)


@router.get("/transactions")
def transactions(limit: int = 50, ctx: GuestContext = Depends(guest_context), db: DB = Depends(get_db)):
    rows = db.scalars(select(WalletTransaction).where(
        WalletTransaction.club_id == ctx.club.id, WalletTransaction.account_id == ctx.account.id
    ).order_by(WalletTransaction.id.desc()).limit(min(max(limit, 1), 200)))
    return [{"id": t.id, "amount": t.amount, "balance_after": t.balance_after, "kind": t.kind,
             "created_at": t.created_at} for t in rows]


@router.post("/sessions")
def start_session(data: GuestSessionIn, ctx: GuestContext = Depends(guest_context), db: DB = Depends(get_db)):
    """Старт сессии на ЭТОМ ПК за счёт баланса гостя."""
    now = utcnow()
    s = sessions.start_session(db, ctx.club, ctx.account, ctx.pc.id, data.tariff_id, now)
    return sessions.session_out(s, now)


@router.post("/sessions/extend")
def extend_session(data: GuestSessionIn, ctx: GuestContext = Depends(guest_context), db: DB = Depends(get_db)):
    """Продление идущей сессии пакетом часов (tariff_id) за счёт баланса."""
    now = utcnow()
    s = sessions.extend_session(db, ctx.club, ctx.account, data.tariff_id, now)
    db.refresh(ctx.account)
    return {**sessions.session_out(s, now), "balance": ctx.account.balance}


@router.post("/sessions/stop")
def stop_session(ctx: GuestContext = Depends(guest_context), db: DB = Depends(get_db)):
    now = utcnow()
    sessions.close_expired(db, ctx.club.id, now)
    s = sessions.active_session_for_account(db, ctx.club.id, ctx.account.id)
    if not s:
        raise ServiceError(404, "У вас нет активной сессии")
    s = sessions.stop_session(db, ctx.club.id, s.id, now, account_id=ctx.account.id)
    db.refresh(ctx.account)
    return {**sessions.session_out(s, now), "balance": ctx.account.balance}


# ---------- бар: меню, заказы ----------
@router.get("/products")
def products(ctx: GuestContext = Depends(guest_context), db: DB = Depends(get_db)):
    """Меню бара: только то, что сейчас можно заказать."""
    return [shop.product_out(p) for p in shop.list_products(db, ctx.club.id, active_only=True)]


@router.post("/orders")
def create_order(data: OrderIn, ctx: GuestContext = Depends(guest_context), db: DB = Depends(get_db)):
    """Заказ с ЭТОГО ПК: деньги списываются с баланса сразу, сотрудник видит, за какой ПК нести."""
    order = shop.create_order(db, ctx.club.id, ctx.pc, ctx.account, data, utcnow())
    db.refresh(ctx.account)
    return {**shop.order_out(order), "balance": ctx.account.balance}


@router.get("/orders")
def my_orders(ctx: GuestContext = Depends(guest_context), db: DB = Depends(get_db)):
    return [shop.order_out(o) for o in shop.guest_orders(db, ctx.club.id, ctx.account.id)]


@router.post("/orders/{order_id}/cancel")
def cancel_order(order_id: int, ctx: GuestContext = Depends(guest_context), db: DB = Depends(get_db)):
    """Отмена своего заказа, пока его не приняли. Деньги возвращаются на баланс."""
    order = shop.cancel_by_guest(db, ctx.club.id, order_id, ctx.account.id, utcnow())
    db.refresh(ctx.account)
    return {**shop.order_out(order), "balance": ctx.account.balance}


# ---------- вызов администратора ----------
@router.post("/calls")
def call_admin(data: CallIn, ctx: GuestContext = Depends(guest_context), db: DB = Depends(get_db)):
    """«Позвать администратора» с этого ПК. Если вызов уже открыт, новый не создаётся."""
    call, already = shop.create_call(db, ctx.club.id, ctx.pc, ctx.account, data.text, utcnow())
    return {**shop.call_out(call), "already_open": already}


@router.get("/calls/current")
def current_call(ctx: GuestContext = Depends(guest_context), db: DB = Depends(get_db)):
    """Есть ли открытый вызов с этого ПК (чтобы меню показывало «администратор вызван» и сняло надпись, когда он подойдёт)."""
    call = shop.open_call(db, ctx.club.id, ctx.pc.id)
    return {"open": shop.call_out(call) if call else None}

