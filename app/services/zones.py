"""Залы клуба как шаблоны: обычный зал, VIP, Premium, PS5, Duo. У зала свои характеристики и свои тарифы; ПК, добавленный в зал, ничего не вводит заново.
Всё строго в пределах клуба.

Какие тарифы видит ПК (applicable_tariffs):
  • у зала ПК есть свои действующие тарифы: только они (в VIP «Час» стоит 200, а не общие 120, и общий «Час» рядом не показывается);
  • своих тарифов у зала нет, или ПК без шаблона: общие тарифы клуба (у которых zone_id пусто), как было до появления залов.
Поэтому клуб, который ничего не настраивал, работает как раньше, а зал «подключает» свои цены, как только в нём заведён первый тариф."""
from datetime import datetime

from sqlalchemy import func, select
from sqlalchemy.orm import Session as DB

from ..errors import ServiceError, commit_or_409
from ..models import PC, Tariff, Zone
from ..schemas import ZoneIn, ZonePatch

SPEC_FIELDS = ("cpu", "gpu", "ram", "monitor", "peripherals", "note")


def zone_out(z: Zone, *, pcs: int | None = None, tariffs: int | None = None) -> dict:
    out = {"id": z.id, "name": z.name, "sort_order": z.sort_order, **{f: getattr(z, f) for f in SPEC_FIELDS}}
    if pcs is not None:
        out["pcs"] = pcs
    if tariffs is not None:
        out["tariffs"] = tariffs
    return out


def list_zones(db: DB, club_id: int) -> list[Zone]:
    return list(db.scalars(select(Zone).where(Zone.club_id == club_id).order_by(Zone.sort_order, Zone.id)))


def get_zone(db: DB, club_id: int, zone_id: int) -> Zone:
    z = db.scalar(select(Zone).where(Zone.id == zone_id, Zone.club_id == club_id))
    if not z:
        raise ServiceError(404, "Зал не найден")
    return z


def counts(db: DB, club_id: int) -> tuple[dict[int, int], dict[int, int]]:
    """Сколько ПК и сколько действующих тарифов в каждом зале клуба."""
    pcs = {zid: n for zid, n in db.execute(select(PC.zone_id, func.count(PC.id)).where(PC.club_id == club_id, PC.zone_id.is_not(None)).group_by(PC.zone_id))}
    tariffs = {zid: n for zid, n in db.execute(select(Tariff.zone_id, func.count(Tariff.id)).where(
        Tariff.club_id == club_id, Tariff.active, Tariff.zone_id.is_not(None)).group_by(Tariff.zone_id))}
    return pcs, tariffs


def create_zone(db: DB, club_id: int, data: ZoneIn, now: datetime) -> Zone:
    if db.scalar(select(Zone.id).where(Zone.club_id == club_id, Zone.name == data.name)):
        raise ServiceError(409, "Зал с таким названием уже есть")
    z = Zone(club_id=club_id, created_at=now, **data.model_dump())
    db.add(z)
    commit_or_409(db, "Зал с таким названием уже есть")
    return z


def update_zone(db: DB, club_id: int, zone_id: int, patch: ZonePatch) -> Zone:
    z = get_zone(db, club_id, zone_id)
    fields = patch.model_dump(exclude_unset=True)
    if "name" in fields and fields["name"] is not None and fields["name"] != z.name:
        if db.scalar(select(Zone.id).where(Zone.club_id == club_id, Zone.name == fields["name"], Zone.id != z.id)):
            raise ServiceError(409, "Зал с таким названием уже есть")
        z.name = fields["name"]
        for pc in db.scalars(select(PC).where(PC.club_id == club_id, PC.zone_id == z.id)):
            pc.zone = z.name                       # зал на карте переименовывается вместе с шаблоном
    for f in SPEC_FIELDS:
        if f in fields:
            setattr(z, f, fields[f])
    if fields.get("sort_order") is not None:
        z.sort_order = fields["sort_order"]
    commit_or_409(db, "Зал с таким названием уже есть")
    return z


def delete_zone(db: DB, club_id: int, zone_id: int) -> None:
    """Зал с ПК удалить нельзя: сначала переведите ПК в другой зал. Тарифы зала отключаются (старые сессии по ним остаются в отчётах)."""
    z = get_zone(db, club_id, zone_id)
    if db.scalar(select(func.count(PC.id)).where(PC.club_id == club_id, PC.zone_id == z.id)):
        raise ServiceError(409, "В зале есть ПК. Сначала переведите их в другой зал")
    for t in db.scalars(select(Tariff).where(Tariff.club_id == club_id, Tariff.zone_id == z.id)):
        t.active = False
        t.zone_id = None
    db.delete(z)
    db.commit()


def assign_pc(db: DB, club_id: int, pc: PC, zone_id: int | None) -> None:
    """Ставит ПК в зал-шаблон (zone_id) или снимает шаблон (None: ПК остаётся в зале на карте, но тарифы общие). Не коммитит.
    Сменился зал: клетка на карте сбрасывается, потому что она была в сетке прежнего зала."""
    if zone_id is None:
        pc.zone_id = None
        return
    z = get_zone(db, club_id, zone_id)
    if pc.zone_id != z.id:
        pc.pos_x = pc.pos_y = None
    pc.zone_id, pc.zone = z.id, z.name


def sync_by_name(db: DB, club_id: int, pc: PC) -> None:
    """ПК перенесли в зал на карте по названию: шаблон берётся у зала с таким названием (или снимается, если такого шаблона нет). Не коммитит."""
    z = db.scalar(select(Zone).where(Zone.club_id == club_id, Zone.name == pc.zone))
    pc.zone_id = z.id if z else None


def applicable_tariffs(db: DB, club_id: int, zone_id: int | None) -> list[Tariff]:
    """Действующие тарифы, которые видит ПК этого зала (см. описание модуля)."""
    if zone_id is not None:
        own = list(db.scalars(select(Tariff).where(Tariff.club_id == club_id, Tariff.zone_id == zone_id, Tariff.active).order_by(Tariff.id)))
        if own:
            return own
    return list(db.scalars(select(Tariff).where(Tariff.club_id == club_id, Tariff.zone_id.is_(None), Tariff.active).order_by(Tariff.id)))


def tariff_applies(db: DB, club_id: int, tariff: Tariff, pc: PC) -> bool:
    return any(t.id == tariff.id for t in applicable_tariffs(db, club_id, pc.zone_id))


def copy_tariffs(db: DB, club_id: int, to_zone_id: int, from_zone_id: int | None) -> int:
    """Копирует действующие тарифы из другого зала (или из общих, если from_zone_id пусто) в зал, у которого своих тарифов ещё нет.
    Потом цены правятся в самом зале: так новый зал не заводят с нуля. Возвращает, сколько тарифов скопировано."""
    to = get_zone(db, club_id, to_zone_id)
    if from_zone_id is not None:
        get_zone(db, club_id, from_zone_id)
        if from_zone_id == to.id:
            raise ServiceError(422, "Нельзя копировать тарифы зала в него же")
    if db.scalar(select(func.count(Tariff.id)).where(Tariff.club_id == club_id, Tariff.zone_id == to.id, Tariff.active)):
        raise ServiceError(409, "В зале уже есть свои тарифы: отключите их или добавьте нужные вручную")
    source = db.scalars(select(Tariff).where(
        Tariff.club_id == club_id, Tariff.active, Tariff.zone_id == from_zone_id if from_zone_id is not None else Tariff.zone_id.is_(None)).order_by(Tariff.id)).all()
    if not source:
        raise ServiceError(409, "Копировать нечего: там нет действующих тарифов")
    for t in source:
        db.add(Tariff(club_id=club_id, name=t.name, kind=t.kind, price=t.price, package_minutes=t.package_minutes,
                      night_start=t.night_start, night_end=t.night_end, day_prices=t.day_prices, zone_id=to.id))
    db.commit()
    return len(source)
