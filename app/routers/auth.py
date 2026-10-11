"""Вход на ПК гостя: регистрация по телефону (SMS), вход гостя и сотрудника, выход, сброс пароля.
Все запросы идут с ключом ПК (X-PC-Key): клуб определяется по нему."""
from fastapi import APIRouter, Depends, Request
from sqlalchemy.orm import Session as DB

from ..db import get_db
from ..deps import PCContext, PCLoginContext, pc_context, pc_login_context
from ..errors import ServiceError
from ..phone import mask_phone
from ..schemas import PCLoginIn, RegisterConfirmIn, RequestCodeIn, ResetConfirmIn
from ..services import accounts, audit, logins, sessions, verification
from ..services.sms import SmsProvider, get_sms
from ..timeutil import utcnow

router = APIRouter(prefix="/api/auth", tags=["вход на ПК"])


def _ip(request: Request) -> str | None:
    return request.client.host if request.client else None


def _club_active(ctx: PCContext) -> None:
    if ctx.club.status != "active":
        raise ServiceError(403, "Клуб приостановлен")


def account_out(acc) -> dict:
    return {"id": acc.id, "role": acc.role, "phone_masked": mask_phone(acc.phone), "balance": acc.balance}


# ---------- регистрация ----------
@router.post("/register/request-code")
def register_request_code(data: RequestCodeIn, request: Request, ctx: PCContext = Depends(pc_context),
                          db: DB = Depends(get_db), sms: SmsProvider = Depends(get_sms)):
    """Шаг 1: гость вводит телефон, ему приходит SMS с кодом.
    Ответ одинаков для любого номера, даже если он уже зарегистрирован (SMS в этом случае не уходит)."""
    _club_active(ctx)
    exists = accounts.get_by_phone(db, ctx.club.id, data.phone) is not None
    wait = verification.request_code(db, sms, ctx.club, ctx.pc, data.phone, "register", deliver=not exists,
                                     ip=_ip(request), now=utcnow())
    return {"resend_after": wait}


@router.post("/register/confirm")
def register_confirm(data: RegisterConfirmIn, ctx: PCContext = Depends(pc_context), db: DB = Depends(get_db)):
    """Шаг 2: код из SMS + пароль → аккаунт создан, гость сразу вошёл на этом ПК (в кабинет)."""
    _club_active(ctx)
    now = utcnow()
    logins.check_can_login(db, ctx.club, ctx.pc, "guest", None, now)
    exists = accounts.get_by_phone(db, ctx.club.id, data.phone) is not None
    verification.consume_code(db, ctx.club.id, data.phone, "register", data.code, now)
    if exists:  # номер уже занят — отвечаем так же, как на неверный код
        raise ServiceError(400, verification.BAD_CODE)
    acc = accounts.create_account(db, ctx.club.id, data.phone, data.password, "guest", now, consent=True)
    token = logins.open_pc_login(db, ctx.club, ctx.pc, acc, now)
    db.commit()
    return {"token": token, "account": account_out(acc)}


# ---------- вход / выход ----------
@router.post("/login")
def login(data: PCLoginIn, request: Request, ctx: PCContext = Depends(pc_context), db: DB = Depends(get_db)):
    """Вход на этом ПК. Гость попадает в кабинет, сотрудник — на обычный рабочий стол (mode=staff_desktop)."""
    _club_active(ctx)
    now = utcnow()
    acc = accounts.authenticate(db, ctx.club.id, data.phone, data.password, now)
    token = logins.open_pc_login(db, ctx.club, ctx.pc, acc, now)
    if acc.role == "staff":
        audit.record(db, ctx.club.id, "staff_unlock", now, actor=acc, pc_id=ctx.pc.id, ip=_ip(request))
    db.commit()
    return {"token": token, "account": account_out(acc), "mode": "staff_desktop" if acc.role == "staff" else "locked"}


@router.post("/logout")
def logout(request: Request, ctx: PCLoginContext = Depends(pc_login_context), db: DB = Depends(get_db)):
    """Выход на этом ПК. Гость с идущей сессией сначала должен её завершить (чтобы деньги не пропали незаметно)."""
    now = utcnow()
    if ctx.account.role == "guest" and sessions.active_session_for_account(db, ctx.club.id, ctx.account.id):
        raise ServiceError(409, "Сначала завершите сессию")
    ctx.login.ended_at = now
    if ctx.account.role == "staff":
        audit.record(db, ctx.club.id, "staff_lock", now, actor=ctx.account, pc_id=ctx.pc.id, ip=_ip(request))
    db.commit()
    return {"ok": True}


# ---------- сброс пароля (только для гостей) ----------
@router.post("/password-reset/request-code")
def reset_request_code(data: RequestCodeIn, request: Request, ctx: PCContext = Depends(pc_context),
                       db: DB = Depends(get_db), sms: SmsProvider = Depends(get_sms)):
    """SMS уходит только на номера гостей. Пароль сотрудника по SMS не сбрасывается (его меняет платформа):
    иначе перехват SIM-карты давал бы доступ к рабочим столам клуба. Ответ одинаков для любого номера."""
    _club_active(ctx)
    acc = accounts.get_by_phone(db, ctx.club.id, data.phone)
    wait = verification.request_code(db, sms, ctx.club, ctx.pc, data.phone, "reset",
                                     deliver=bool(acc and acc.role == "guest"), ip=_ip(request), now=utcnow())
    return {"resend_after": wait}


@router.post("/password-reset/confirm")
def reset_confirm(data: ResetConfirmIn, request: Request, ctx: PCContext = Depends(pc_context),
                  db: DB = Depends(get_db)):
    _club_active(ctx)
    now = utcnow()
    acc = accounts.get_by_phone(db, ctx.club.id, data.phone)
    verification.consume_code(db, ctx.club.id, data.phone, "reset", data.code, now)
    if not acc or acc.role != "guest":
        raise ServiceError(400, verification.BAD_CODE)
    accounts.set_password(db, acc, data.new_password, now)
    audit.record(db, ctx.club.id, "password_reset", now, actor=acc, pc_id=ctx.pc.id, ip=_ip(request))
    db.commit()
    return {"ok": True}
