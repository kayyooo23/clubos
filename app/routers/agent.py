"""Что видит клиент на ПК гостя. ПК определяется по ключу (X-PC-Key), id в URL не нужен."""
from fastapi import APIRouter, Depends, Query
from fastapi.responses import Response
from sqlalchemy import select
from sqlalchemy.orm import Session as DB

from ..db import get_db
from ..deps import PCContext, pc_context
from ..errors import ServiceError
from ..models import Account, CatalogApp, Tariff
from ..schemas import HardwareIn, ProgramsIn
from ..services import ads, catalog, covers, fleet, logins, sessions
from ..timeutil import utcnow
from .auth import account_out

router = APIRouter(prefix="/api/agent", tags=["клиент на ПК"])


@router.get("/state")
def state(ctx: PCContext = Depends(pc_context), db: DB = Depends(get_db), banner: int | None = Query(None, alias="ads")):
    """Состояние ПК (см. _state). Если администратор попросил обновить список программ, в ответе есть scan_programs: служба соберёт и пришлёт его."""
    out = _state(ctx, db, banner)
    if fleet.scan_requested(ctx.pc):
        out = {**out, "scan_programs": True}
    return out


def _state(ctx: PCContext, db: DB, banner: int | None):
    """Клиент дёргает это раз в несколько секунд. Заодно это heartbeat (ПК на связи).
    Параметр ads: 1 = на экране сейчас рекламная заставка, 0 = заставки нет (гость у формы входа), не задан = старый клиент (считаем показ, пока ПК заблокирован).
    mode:
      locked         — экран блокировки; если в ответе есть account, гость вошёл и видит кабинет (баланс, тарифы);
      guest_session  — идёт игровая сессия, ПК разблокирован;
      staff_desktop  — вошёл сотрудник: обычный рабочий стол, без тарифа и оплаты.
    club_name и pc_name — для экрана блокировки (чей клуб, какой это ПК)."""
    now = utcnow()
    ctx.pc.last_seen = now
    names = {"club_name": ctx.club.name, "pc_name": ctx.pc.name}  # экран блокировки показывает, чей это клуб и какой ПК
    if ctx.club.status != "active":
        db.commit()
        return {**names, "mode": "locked", "account": None, "session": None, "message": "Клуб приостановлен"}
    sessions.close_expired(db, ctx.club.id, now)  # коммитит, заодно сохраняет last_seen
    cur = logins.current_login(db, ctx.club, ctx.pc.id, now)  # закрывает устаревший вход (бездействие)
    s = sessions.active_session(db, ctx.club.id, ctx.pc.id)
    db.commit()

    if cur and cur[1].role == "staff":
        ctx.pc.ads_seen_at = None
        db.commit()
        return {**names, "mode": "staff_desktop", "account": {"role": "staff"}, "session": None}
    if s or banner == 0:
        ctx.pc.ads_seen_at = None          # идёт игра или гость у формы входа: баннера на экране нет, показ не считаем
    else:
        ads.accrue(db, ctx.club, ctx.pc, now)
    db.commit()
    account = cur[1] if cur else (db.get(Account, s.account_id) if s else None)
    out = {**names, "mode": "guest_session" if s else "locked",
           "account": account_out(account) if account else None,
           "session": sessions.session_out(s, now) if s else None}
    if ctx.pc.maintenance and not s:
        out["message"] = "ПК на обслуживании"
    return out


@router.post("/hardware")
def hardware(data: HardwareIn, ctx: PCContext = Depends(pc_context), db: DB = Depends(get_db)):
    """Служба на ПК присылает характеристики железа (при запуске и раз в несколько часов)."""
    changed = fleet.set_hardware(db, ctx.pc, data, utcnow())
    db.commit()
    return {"ok": True, "changed": changed}


@router.post("/programs")
def programs(data: ProgramsIn, ctx: PCContext = Depends(pc_context), db: DB = Depends(get_db)):
    """Служба присылает программы, найденные на ПК (ярлыки в меню «Пуск»): при запуске, раз в пару часов и когда администратор просил обновить."""
    now = utcnow()
    count = fleet.set_programs(db, ctx.pc, data, now)
    db.flush()
    covers.fill_from_programs(db, ctx.club.id, now)           # у программ меню без картинки появляется иконка с этого ПК
    db.commit()
    return {"ok": True, "accepted": count}


@router.get("/ads")
def agent_ads(ctx: PCContext = Depends(pc_context), db: DB = Depends(get_db)):
    """Баннеры для экрана блокировки по порядку ротации: пусто, если клуб не включил рекламу. Сами картинки отдаёт /ads/{id}/image."""
    out = []
    for a in ads.ads_for_club(db, ctx.club, utcnow()):
        image = ads.get_image(db, a.id)
        if image is not None:
            out.append(ads.screen_out(a, image))
    return out


@router.get("/ads/{ad_id}/image")
def agent_ad_image(ad_id: int, ctx: PCContext = Depends(pc_context), db: DB = Depends(get_db)):
    """Картинка баннера. Отдаём только тот, что сейчас действительно идёт в этом клубе: чужие и выключенные не скачать."""
    if ad_id not in {a.id for a in ads.ads_for_club(db, ctx.club, utcnow())}:
        raise ServiceError(404, "Баннер не найден")
    image = ads.get_image(db, ad_id)
    if image is None:
        raise ServiceError(404, "Баннер не найден")
    return Response(content=image.data, media_type=image.content_type, headers={"ETag": f'"{image.version}"'})


@router.get("/apps")
def apps(ctx: PCContext = Depends(pc_context), db: DB = Depends(get_db)):
    """Каталог игр и программ для меню гостя. Скрытые записи сюда не попадают."""
    meta = covers.meta_for_club(db, ctx.club.id)
    return [catalog.app_out(a, cover=meta.get(a.id)) for a in catalog.list_apps(db, ctx.club.id, active_only=True)]


@router.get("/apps/{app_id}/cover")
def agent_app_cover(app_id: int, ctx: PCContext = Depends(pc_context), db: DB = Depends(get_db)):
    """Обложка игры. Отдаём только игры, которые сейчас видны в меню этого клуба."""
    a = db.scalar(select(CatalogApp).where(CatalogApp.id == app_id, CatalogApp.club_id == ctx.club.id, CatalogApp.active))
    cover = covers.get_cover(db, a.id) if a else None
    if cover is None:
        raise ServiceError(404, "Обложка не найдена")
    return Response(content=cover.data, media_type=cover.content_type, headers={"ETag": f'"{cover.version}"'})


@router.get("/tariffs")
def tariffs(ctx: PCContext = Depends(pc_context), db: DB = Depends(get_db)):
    return [{"id": t.id, "name": t.name, "kind": t.kind, "price": t.price,
             "package_minutes": t.package_minutes, "night_start": t.night_start, "night_end": t.night_end}
            for t in db.scalars(select(Tariff).where(Tariff.club_id == ctx.club.id, Tariff.active))]
