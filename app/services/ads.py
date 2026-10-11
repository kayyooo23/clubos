"""Реклама партнёров на экране блокировки: баннеры-картинки, которые сменяют друг друга.
Баннеры общие для платформы, заводит их владелец платформы (загружает картинку рекламодателя). Клуб включает показ у себя и получает долю дохода.
Показывается только баннер с картинкой. Время показа считается на сервере по опросам ПК (клиент ничего о показах не сообщает, поэтому накрутить нельзя)."""
import hashlib
from datetime import date, datetime, timedelta

from pydantic import ValidationError
from sqlalchemy import func, select
from sqlalchemy.exc import IntegrityError
from sqlalchemy.orm import Session as DB

from ..errors import ServiceError
from ..models import Ad, AdImage, AdStat, Club, PC
from ..schemas import AdIn, AdPatch
from . import images

MAX_ADS = 100            # потолок записей
MAX_ON_SCREEN = 12       # сколько баннеров одновременно в ротации: больше гость всё равно не дождётся
ACCRUE_EVERY = 30        # время показа записываем кусками не чаще раза в 30 секунд на ПК (не пишем в базу на каждый опрос)
ACCRUE_CAP = 60          # и не больше минуты за раз: ПК, пропавший на час, не принесёт час показа


# ---------- объявления ----------
def ad_out(a: Ad, *, seconds: int | None = None, image: AdImage | None = None) -> dict:
    img = image
    out = {"id": a.id, "text": a.text, "advertiser": a.advertiser, "erid": a.erid, "active": a.active,
           "starts_at": a.starts_at, "ends_at": a.ends_at, "sort_order": a.sort_order, "duration_seconds": a.duration_seconds,
           "has_image": img is not None, "width": img.width if img else None, "height": img.height if img else None,
           "version": img.version if img else None}
    if seconds is not None:
        out["shown_hours"] = round(seconds / 3600, 2)
    return out


def screen_out(a: Ad, image: AdImage) -> dict:
    """Что видит клиент на ПК: только то, что нужно показать. Название для владельца гостю не отдаём."""
    return {"id": a.id, "advertiser": a.advertiser, "erid": a.erid, "seconds": a.duration_seconds,
            "width": image.width, "height": image.height, "version": image.version}


def get_image(db: DB, ad_id: int) -> AdImage | None:
    return db.get(AdImage, ad_id)


def set_image(db: DB, ad_id: int, data: bytes, now: datetime) -> AdImage:
    """Загружает или заменяет картинку баннера. Тип и размер проверяются по самим байтам, а не по тому, что сказал браузер."""
    a = get_ad(db, ad_id)
    info = images.inspect_image(data)
    version = hashlib.sha256(data).hexdigest()[:16]
    row = db.get(AdImage, a.id)
    if row is None:
        row = AdImage(ad_id=a.id, content_type=info.content_type, width=info.width, height=info.height, version=version, data=data, created_at=now)
        db.add(row)
    else:
        row.content_type, row.width, row.height, row.version, row.data, row.created_at = info.content_type, info.width, info.height, version, data, now
    db.flush()
    return row


def image_meta(db: DB) -> dict[int, AdImage]:
    """Картинки без байтов: для списков (какой баннер с картинкой, какого размера)."""
    from sqlalchemy.orm import defer
    return {i.ad_id: i for i in db.scalars(select(AdImage).options(defer(AdImage.data)))}


def get_ad(db: DB, ad_id: int) -> Ad:
    a = db.get(Ad, ad_id)
    if not a:
        raise ServiceError(404, "Объявление не найдено")
    return a


def _first_error(e: ValidationError) -> ServiceError:
    err = e.errors()[0]
    field = ".".join(str(p) for p in err["loc"] if p != "__root__")
    msg = str(err["msg"]).removeprefix("Value error, ")
    return ServiceError(422, f"{field}: {msg}" if field else msg)


def _naive_utc(dt: datetime | None) -> datetime | None:
    from datetime import timezone
    return dt.astimezone(timezone.utc).replace(tzinfo=None) if dt is not None and dt.tzinfo else dt


def create_ad(db: DB, data: AdIn, now: datetime) -> Ad:
    if (db.scalar(select(func.count(Ad.id))) or 0) >= MAX_ADS:
        raise ServiceError(409, f"Уже {MAX_ADS} объявлений. Удалите старые")
    a = Ad(created_at=now, text=data.text, advertiser=data.advertiser, erid=data.erid, active=data.active,
           starts_at=_naive_utc(data.starts_at), ends_at=_naive_utc(data.ends_at), sort_order=data.sort_order,
           duration_seconds=data.duration_seconds)
    db.add(a)
    db.flush()
    return a


def update_ad(db: DB, ad_id: int, patch: AdPatch) -> Ad:
    a = get_ad(db, ad_id)
    merged = {"text": a.text, "advertiser": a.advertiser, "erid": a.erid, "active": a.active,
              "starts_at": a.starts_at, "ends_at": a.ends_at, "sort_order": a.sort_order, "duration_seconds": a.duration_seconds}
    merged.update(patch.model_dump(exclude_unset=True))
    try:
        checked = AdIn.model_validate(merged)
    except ValidationError as e:
        raise _first_error(e)
    a.text, a.advertiser, a.erid, a.active, a.sort_order = checked.text, checked.advertiser, checked.erid, checked.active, checked.sort_order
    a.duration_seconds = checked.duration_seconds
    a.starts_at, a.ends_at = _naive_utc(checked.starts_at), _naive_utc(checked.ends_at)
    return a


def delete_ad(db: DB, ad_id: int) -> None:
    a = get_ad(db, ad_id)
    if db.scalar(select(func.count(AdStat.id)).where(AdStat.ad_id == a.id)):
        raise ServiceError(409, "По этому баннеру уже есть показы, на них строится отчёт. Выключите его, а не удаляйте")
    image = db.get(AdImage, a.id)
    if image is not None:
        db.delete(image)
        db.flush()          # сначала картинка: на неё ссылается баннер, а внешние ключи включены
    db.delete(a)


def active_ads(db: DB, now: datetime) -> list[Ad]:
    """Баннеры, которые можно показывать прямо сейчас: включены, попадают в окно показа и у них загружена картинка."""
    q = (select(Ad).join(AdImage, AdImage.ad_id == Ad.id)
         .where(Ad.active, (Ad.starts_at.is_(None)) | (Ad.starts_at <= now), (Ad.ends_at.is_(None)) | (Ad.ends_at > now)))
    return list(db.scalars(q.order_by(Ad.sort_order, Ad.id).limit(MAX_ON_SCREEN)))


def ads_for_club(db: DB, club: Club, now: datetime) -> list[Ad]:
    return active_ads(db, now) if club.ads_enabled else []


# ---------- настройки клуба ----------
def club_ads_out(club: Club) -> dict:
    return {"enabled": club.ads_enabled, "club_share": club.ads_club_share}


# ---------- учёт показа ----------
def _club_day(club: Club, now: datetime) -> date:
    return (now + timedelta(hours=club.utc_offset)).date()


def accrue(db: DB, club: Club, pc: PC, now: datetime) -> int:
    """Вызывается при опросе ПК, пока на нём виден экран блокировки. Возвращает, сколько секунд записано (0 чаще всего)."""
    ads = ads_for_club(db, club, now)
    if not ads:
        pc.ads_seen_at = None
        return 0
    if pc.ads_seen_at is None:
        pc.ads_seen_at = now
        return 0
    delta = (now - pc.ads_seen_at).total_seconds()
    if delta < ACCRUE_EVERY:
        return 0
    seconds = int(min(delta, ACCRUE_CAP))
    pc.ads_seen_at = now
    day = _club_day(club, now)
    for ad in ads:
        _add_seconds(db, club.id, ad.id, day, seconds)
    return seconds


def _add_seconds(db: DB, club_id: int, ad_id: int, day: date, seconds: int) -> None:
    row = db.scalar(select(AdStat).where(AdStat.club_id == club_id, AdStat.ad_id == ad_id, AdStat.day == day))
    if row is not None:
        row.seconds += seconds
        return
    try:
        with db.begin_nested():
            db.add(AdStat(club_id=club_id, ad_id=ad_id, day=day, seconds=seconds))
            db.flush()
    except IntegrityError:   # два ПК одного клуба записали одновременно: прибавляем к уже созданной строке
        row = db.scalar(select(AdStat).where(AdStat.club_id == club_id, AdStat.ad_id == ad_id, AdStat.day == day))
        if row is not None:
            row.seconds += seconds


# ---------- отчёты ----------
def exposure_report(db: DB, since: date, until: date) -> dict:
    """Время показа за период: по объявлениям (с разбивкой по клубам) и по клубам (сколько часов экран блокировки показывал рекламу)."""
    rows = db.execute(select(AdStat.ad_id, AdStat.club_id, AdStat.day, AdStat.seconds)
                      .where(AdStat.day >= since, AdStat.day <= until)).all()
    ads = {a.id: a for a in db.scalars(select(Ad))}
    clubs = {c.id: c for c in db.scalars(select(Club))}
    per_ad: dict[int, dict[int, int]] = {}
    club_day: dict[tuple[int, date], int] = {}
    for ad_id, club_id, day, seconds in rows:
        per_ad.setdefault(ad_id, {}).setdefault(club_id, 0)
        per_ad[ad_id][club_id] += seconds
        key = (club_id, day)
        club_day[key] = max(club_day.get(key, 0), seconds)   # все показанные в этот день объявления идут одной строкой: экранное время клуба = максимум
    per_club: dict[int, int] = {}
    for (club_id, _), seconds in club_day.items():
        per_club[club_id] = per_club.get(club_id, 0) + seconds
    return {
        "since": since, "until": until,
        "per_ad": [{"ad_id": ad_id, "text": ads[ad_id].text, "advertiser": ads[ad_id].advertiser,
                    "hours": round(sum(by_club.values()) / 3600, 2),
                    "clubs": [{"club_id": cid, "club": clubs[cid].name, "hours": round(sec / 3600, 2)} for cid, sec in sorted(by_club.items())]}
                   for ad_id, by_club in sorted(per_ad.items()) if ad_id in ads],
        "per_club": [{"club_id": cid, "club": clubs[cid].name, "enabled": clubs[cid].ads_enabled, "club_share": clubs[cid].ads_club_share,
                      "screen_hours": round(sec / 3600, 2)} for cid, sec in sorted(per_club.items()) if cid in clubs],
    }


def club_exposure_hours(db: DB, club_id: int, since: date) -> float:
    """Экранное время клуба с рекламой с даты since: для карточки в админке клуба."""
    rows = db.execute(select(AdStat.day, func.max(AdStat.seconds)).where(AdStat.club_id == club_id, AdStat.day >= since)
                      .group_by(AdStat.day)).all()
    return round(sum(sec for _, sec in rows) / 3600, 2)
