"""Управление клубами — только для владельца платформы (ключ X-Platform-Key)."""
from datetime import date, timedelta

import hashlib

from fastapi import APIRouter, Depends, Request
from fastapi.responses import Response
from sqlalchemy import func, select
from sqlalchemy.orm import Session as DB

from ..db import get_db
from ..deps import platform_only
from ..errors import ServiceError
from ..models import Ad, AdStat, Club
from ..schemas import AdIn, AdPatch, ClubAdsIn, ClubIn, ClubStatusIn, CopyAppsIn, ReleaseIn, ReleasePatch, StaffPasswordResetIn
from ..services import accounts, ads, audit, catalog, clubs, images, ops, releases
from ..timeutil import utcnow

router = APIRouter(prefix="/api/platform", tags=["платформа"], dependencies=[Depends(platform_only)])


def club_out(c: Club) -> dict:
    return {"id": c.id, "slug": c.slug, "name": c.name, "utc_offset": c.utc_offset, "status": c.status,
            "ads_enabled": c.ads_enabled, "ads_club_share": c.ads_club_share}


@router.post("/clubs")
def create_club(data: ClubIn, db: DB = Depends(get_db)):
    club = clubs.create_club_with_owner(db, data.slug, data.name, data.utc_offset,
                                        data.owner_phone, data.owner_password, utcnow())
    return club_out(club)


@router.get("/status")
def server_status(db: DB = Depends(get_db)):
    """Состояние сервера для владельца платформы: версия схемы базы, клубы и ПК на связи, размер базы, резервные копии и предупреждения."""
    return ops.status(db, utcnow())


@router.get("/clubs")
def list_clubs(db: DB = Depends(get_db)):
    return [club_out(c) for c in db.scalars(select(Club).order_by(Club.id))]


@router.post("/clubs/{club_id}/reset-staff-password")
def reset_staff_password(club_id: int, data: StaffPasswordResetIn, db: DB = Depends(get_db)):
    """Сотрудник забыл пароль: по SMS пароли сотрудников не сбрасываются, это делает только платформа."""
    acc = accounts.get_by_phone(db, club_id, data.phone)
    if not acc or acc.role != "staff":
        raise ServiceError(404, "Сотрудник не найден")
    now = utcnow()
    accounts.set_password(db, acc, data.new_password, now)
    audit.record(db, club_id, "staff_password_reset_by_platform", now, details=f"account={acc.id}")
    db.commit()
    return {"ok": True}


@router.post("/clubs/{club_id}/copy-apps")
def copy_apps(club_id: int, data: CopyAppsIn, db: DB = Depends(get_db)):
    """Скопировать каталог игр из одного клуба в другой: так у всех клубов сети один и тот же список."""
    now = utcnow()
    count = catalog.copy_catalog(db, data.from_club_id, club_id, now, replace=data.replace)
    audit.record(db, club_id, "apps_copied", now, details=f"from_club={data.from_club_id} count={count}")
    db.commit()
    return {"copied": count}


@router.patch("/clubs/{club_id}")
def set_club_status(club_id: int, data: ClubStatusIn, db: DB = Depends(get_db)):
    """suspended: ПК клуба блокируются, сотрудники не могут войти (например, при неоплате лицензии)."""
    club = db.get(Club, club_id)
    if not club:
        raise ServiceError(404, "Клуб не найден")
    club.status = data.status
    db.commit()
    return club_out(club)


# ---------- реклама на экранах блокировки ----------
@router.get("/ads")
def list_ads(db: DB = Depends(get_db)):
    """Все объявления, включая выключенные, с общим временем показа."""
    totals = dict(db.execute(select(AdStat.ad_id, func.coalesce(func.sum(AdStat.seconds), 0)).group_by(AdStat.ad_id)).all())
    meta = ads.image_meta(db)
    return [ads.ad_out(a, seconds=int(totals.get(a.id, 0)), image=meta.get(a.id)) for a in db.scalars(select(Ad).order_by(Ad.sort_order, Ad.id))]


@router.post("/ads")
def create_ad(data: AdIn, db: DB = Depends(get_db)):
    a = ads.create_ad(db, data, utcnow())
    db.commit()
    return ads.ad_out(a, seconds=0)   # картинку загружают следующим запросом: PUT /ads/{id}/image


@router.patch("/ads/{ad_id}")
def update_ad(ad_id: int, data: AdPatch, db: DB = Depends(get_db)):
    a = ads.update_ad(db, ad_id, data)
    db.commit()
    return ads.ad_out(a, image=ads.image_meta(db).get(a.id))


@router.delete("/ads/{ad_id}")
def delete_ad(ad_id: int, db: DB = Depends(get_db)):
    ads.delete_ad(db, ad_id)
    db.commit()
    return {"ok": True}


@router.put("/ads/{ad_id}/image")
async def put_ad_image(ad_id: int, request: Request, db: DB = Depends(get_db)):
    """Загрузить или заменить картинку баннера: тело запроса это сам файл (PNG или JPEG, до 2 МБ, каждая сторона от 100 до 4096 пикселей)."""
    declared = request.headers.get("content-length")
    if declared and declared.isdigit() and int(declared) > images.MAX_BYTES:
        raise ServiceError(413, f"Файл больше {images.MAX_BYTES // (1024 * 1024)} МБ: сожмите картинку")
    data = await request.body()
    ads.set_image(db, ad_id, data, utcnow())
    db.commit()
    a = ads.get_ad(db, ad_id)
    return ads.ad_out(a, image=ads.image_meta(db).get(a.id))


@router.get("/ads/{ad_id}/image")
def get_ad_image(ad_id: int, db: DB = Depends(get_db)):
    """Картинка баннера для предпросмотра в панели платформы."""
    image = ads.get_image(db, ad_id)
    if image is None:
        raise ServiceError(404, "У баннера нет картинки")
    return Response(content=image.data, media_type=image.content_type)


@router.get("/ads/report")
def ads_report(since: date | None = None, until: date | None = None, db: DB = Depends(get_db)):
    """Время показа за период (по умолчанию последние 30 дней): по объявлениям и по клубам. На этом строится расчёт с рекламодателями и клубами."""
    until = until or date.today()
    since = since or until - timedelta(days=30)
    if since > until:
        raise ServiceError(422, "Начало периода позже конца")
    return ads.exposure_report(db, since, until)


@router.post("/clubs/{club_id}/ads")
def set_club_ads(club_id: int, data: ClubAdsIn, db: DB = Depends(get_db)):
    """Включить или выключить рекламу в клубе и задать долю клуба (проценты)."""
    club = db.get(Club, club_id)
    if not club:
        raise ServiceError(404, "Клуб не найден")
    if data.enabled is not None:
        club.ads_enabled = data.enabled
    if data.club_share is not None:
        club.ads_club_share = data.club_share
    db.commit()
    return club_out(club)



# ---------- версии клиента (автообновление) ----------
@router.get("/releases")
def list_releases(db: DB = Depends(get_db)):
    """Версии клиента и сколько ПК сейчас работает на каждой."""
    return releases.list_out(db)


@router.post("/releases")
def create_release(data: ReleaseIn, db: DB = Depends(get_db)):
    """Завести версию: сведения из файла sign-release.ps1. Версия не раздаётся, пока не загружен пакет и она не включена."""
    r = releases.create(db, data, utcnow())
    db.commit()
    return releases.release_out(r)


@router.put("/releases/{release_id}/package")
async def put_release_package(release_id: int, request: Request, db: DB = Depends(get_db)):
    """Загрузить пакет версии: тело запроса это сам zip. Размер и sha256 должны совпасть с заявленными при создании версии. Файл пишется на диск потоком."""
    r = releases.get(db, release_id)
    releases.check_can_upload(r)
    declared = request.headers.get("content-length")
    if declared and declared.isdigit() and int(declared) != r.size:
        raise ServiceError(422, f"Размер файла {declared} не совпадает с заявленным ({r.size})")
    releases.release_dir().mkdir(parents=True, exist_ok=True)
    part = releases.part_path(r)
    digest, size = hashlib.sha256(), 0
    try:
        with open(part, "wb") as out:
            async for chunk in request.stream():
                size += len(chunk)
                if size > r.size:
                    raise ServiceError(422, "Файл больше заявленного размера")
                digest.update(chunk)
                out.write(chunk)
    except BaseException:
        part.unlink(missing_ok=True)
        raise
    releases.finish_upload(db, r, part, digest.hexdigest(), size, utcnow())
    db.commit()
    return releases.release_out(r)


@router.patch("/releases/{release_id}")
def update_release(release_id: int, data: ReleasePatch, db: DB = Depends(get_db)):
    """Включить или выключить версию, поменять долю ПК (rollout_percent) и список клубов (club_ids), заметку."""
    r = releases.patch(db, release_id, data)
    db.commit()
    return releases.release_out(r)


@router.delete("/releases/{release_id}")
def delete_release(release_id: int, db: DB = Depends(get_db)):
    releases.delete(db, release_id)
    db.commit()
    return {"ok": True}
