"""Вход сотрудников в админку (без привязки к ПК). Вход сотрудника на ПК гостя — отдельный этап."""
from datetime import timedelta

from fastapi import APIRouter, Depends, Request
from sqlalchemy import select
from sqlalchemy.orm import Session as DB

from .. import config
from ..db import get_db
from ..deps import StaffContext, staff_context
from ..errors import ServiceError
from ..models import Club
from ..phone import mask_phone
from ..schemas import StaffLoginIn
from ..security import burn_password_check
from ..services import accounts, audit
from ..timeutil import utcnow

router = APIRouter(prefix="/api/admin/auth", tags=["админка: вход"])


@router.post("/login")
def login(data: StaffLoginIn, request: Request, db: DB = Depends(get_db)):
    now = utcnow()
    club = db.scalar(select(Club).where(Club.slug == data.club))
    if not club:
        burn_password_check(data.password)
        raise ServiceError(401, accounts.BAD_CREDENTIALS)
    acc = accounts.authenticate(db, club.id, data.phone, data.password, now)
    if acc.role != "staff":
        raise ServiceError(401, accounts.BAD_CREDENTIALS)  # гость не должен узнать, что номер существует
    if club.status != "active":
        raise ServiceError(403, "Клуб приостановлен")
    token = accounts.open_login(db, acc, None, now, timedelta(hours=config.STAFF_TOKEN_HOURS))
    audit.record(db, club.id, "admin_login", now, actor=acc, ip=request.client.host if request.client else None)
    db.commit()
    return {"token": token, "token_type": "bearer", "club": club.slug}


@router.post("/logout")
def logout(ctx: StaffContext = Depends(staff_context), db: DB = Depends(get_db)):
    ctx.login.ended_at = utcnow()
    db.commit()
    return {"ok": True}


@router.get("/me")
def me(ctx: StaffContext = Depends(staff_context)):
    return {"phone": mask_phone(ctx.account.phone), "role": ctx.account.role,
            "club": {"id": ctx.club.id, "slug": ctx.club.slug, "name": ctx.club.name, "utc_offset": ctx.club.utc_offset}}
