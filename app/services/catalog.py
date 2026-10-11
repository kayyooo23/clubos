"""Каталог игр и программ для меню гостя. Всё строго в пределах клуба (club_id берётся из токена или ключа ПК)."""
from datetime import datetime

from pydantic import ValidationError
from sqlalchemy import delete, func, select
from sqlalchemy.orm import Session as DB

from ..errors import ServiceError
from ..models import AppCover, CatalogApp, Club
from ..schemas import AppIn, AppPatch

MAX_APPS_PER_CLUB = 300  # потолок, чтобы ошибка скрипта не раздула каталог и ответ клиенту


def app_out(a: CatalogApp, *, admin: bool = False, cover: tuple[str, str] | None = None) -> dict:
    """cover: (version, source) обложки, если она есть (см. covers.meta_for_club). Клиент по version решает, качать ли картинку заново."""
    out = {"id": a.id, "title": a.title, "tab": a.tab, "category": a.category, "age_rating": a.age_rating,
           "launch_kind": a.launch_kind, "launch_target": a.launch_target, "cover_version": cover[0] if cover else None}
    if admin:
        out.update(sort_order=a.sort_order, active=a.active, cover_source=cover[1] if cover else None)
    return out


def list_apps(db: DB, club_id: int, *, active_only: bool) -> list[CatalogApp]:
    q = select(CatalogApp).where(CatalogApp.club_id == club_id)
    if active_only:
        q = q.where(CatalogApp.active)
    return list(db.scalars(q.order_by(CatalogApp.sort_order, CatalogApp.id)))


def get_app(db: DB, club_id: int, app_id: int) -> CatalogApp:
    a = db.scalar(select(CatalogApp).where(CatalogApp.id == app_id, CatalogApp.club_id == club_id))
    if not a:
        raise ServiceError(404, "Игра не найдена")
    return a


def _first_error(e: ValidationError) -> ServiceError:
    err = e.errors()[0]
    field = ".".join(str(p) for p in err["loc"] if p != "__root__")
    msg = str(err["msg"]).removeprefix("Value error, ")
    return ServiceError(422, f"{field}: {msg}" if field else msg)


def _count(db: DB, club_id: int) -> int:
    return db.scalar(select(func.count(CatalogApp.id)).where(CatalogApp.club_id == club_id)) or 0


def create_app(db: DB, club_id: int, data: AppIn, now: datetime) -> CatalogApp:
    if _count(db, club_id) >= MAX_APPS_PER_CLUB:
        raise ServiceError(409, f"В каталоге уже {MAX_APPS_PER_CLUB} записей, это предел")
    a = CatalogApp(club_id=club_id, created_at=now, **data.model_dump())
    db.add(a)
    db.flush()
    return a


def update_app(db: DB, club_id: int, app_id: int, patch: AppPatch) -> CatalogApp:
    a = get_app(db, club_id, app_id)
    merged = {"title": a.title, "tab": a.tab, "category": a.category, "age_rating": a.age_rating,
              "launch_kind": a.launch_kind, "launch_target": a.launch_target,
              "sort_order": a.sort_order, "active": a.active}
    merged.update(patch.model_dump(exclude_unset=True))
    try:
        checked = AppIn.model_validate(merged)  # после правки запись снова проверяется целиком
    except ValidationError as e:
        raise _first_error(e)
    old_target = a.launch_target.lower()
    for field, value in checked.model_dump().items():
        setattr(a, field, value)
    # иконка с ПК принадлежит программе по пути: путь или вкладку поменяли, значит иконка уже не та (новая подставится при следующем отчёте или сохранении)
    cover = db.get(AppCover, a.id)
    if cover is not None and cover.source == "program" and not (a.tab == "apps" and a.launch_kind == "exe" and a.launch_target.lower() == old_target):
        db.delete(cover)
        db.flush()
    return a


def delete_app(db: DB, club_id: int, app_id: int) -> None:
    a = get_app(db, club_id, app_id)
    cover = db.get(AppCover, a.id)
    if cover is not None:
        db.delete(cover)
        db.flush()          # сначала обложка: на игру ссылается она, а внешние ключи включены
    db.delete(a)


def copy_catalog(db: DB, from_club_id: int, to_club_id: int, now: datetime, *, replace: bool) -> int:
    """Копирует каталог одного клуба в другой (так «все наши клубы с одним списком игр» настраиваются за секунду).
    Копии независимы: потом каждый клуб правит свой список сам. Возвращает, сколько записей скопировано."""
    if from_club_id == to_club_id:
        raise ServiceError(422, "Нельзя копировать каталог в тот же клуб")
    if not db.get(Club, from_club_id) or not db.get(Club, to_club_id):
        raise ServiceError(404, "Клуб не найден")
    source = list_apps(db, from_club_id, active_only=False)
    if replace:
        old = select(CatalogApp.id).where(CatalogApp.club_id == to_club_id)
        db.execute(delete(AppCover).where(AppCover.app_id.in_(old)))
        db.execute(delete(CatalogApp).where(CatalogApp.club_id == to_club_id))
    elif _count(db, to_club_id) + len(source) > MAX_APPS_PER_CLUB:
        raise ServiceError(409, f"В каталоге получателя не поместится больше {MAX_APPS_PER_CLUB} записей")
    copies = []
    for a in source:
        copy = CatalogApp(club_id=to_club_id, title=a.title, tab=a.tab, category=a.category, age_rating=a.age_rating,
                          launch_kind=a.launch_kind, launch_target=a.launch_target, sort_order=a.sort_order,
                          active=a.active, created_at=now)
        db.add(copy)
        copies.append((a, copy))
    db.flush()
    for original, copy in copies:        # обложки едут вместе с играми: у сети клубов одинаковое меню
        cover = db.get(AppCover, original.id)
        if cover is not None:
            db.add(AppCover(app_id=copy.id, content_type=cover.content_type, width=cover.width, height=cover.height,
                            version=cover.version, source=cover.source, data=cover.data, created_at=now))
    return len(source)


# Стартовый набор: самые популярные бесплатные и массовые игры Steam + сам Steam для тех, кто играет в свою библиотеку.
# Запись можно скрыть или поправить в админке; номера игр берутся со страницы магазина (store.steampowered.com/app/<номер>).
STARTER_APPS = [
    ("Dota 2", "MOBA", 12, "570"), ("Counter-Strike 2", "Шутеры", 16, "730"),
    ("PUBG: Battlegrounds", "Шутеры", 16, "578080"), ("Apex Legends", "Шутеры", 16, "1172470"),
    ("Delta Force", "Шутеры", 16, "2507950"), ("Marvel Rivals", "Шутеры", 12, "2767030"),
    ("Rust", "Выживание", 18, "252490"), ("Team Fortress 2", "Шутеры", 16, "440"),
    ("War Thunder", "Симуляторы", 12, "236390"), ("Naraka: Bladepoint", "Экшены", 16, "1203220"),
    ("Warframe", "Экшены", 16, "230410"), ("Path of Exile", "RPG", 18, "238960"),
    ("Grand Theft Auto V", "Экшены", 18, "271590"),
]


def seed_starter_catalog(db: DB, club_id: int, now: datetime) -> int:
    """Кладёт стартовый набор в пустой каталог клуба. Если там уже что-то есть, ничего не делает."""
    if _count(db, club_id):
        return 0
    for i, (title, category, age, steam_id) in enumerate(STARTER_APPS):
        db.add(CatalogApp(club_id=club_id, title=title, tab="games", category=category, age_rating=age,
                          launch_kind="steam", launch_target=steam_id, sort_order=10 * (i + 1), created_at=now))
    db.add(CatalogApp(club_id=club_id, title="Steam", tab="apps", category="Лаунчеры", launch_kind="exe",
                      launch_target=r"C:\Program Files (x86)\Steam\steam.exe", sort_order=10, created_at=now))
    db.add(CatalogApp(club_id=club_id, title="Google Chrome", tab="apps", category="Браузеры", launch_kind="exe",
                      launch_target=r"C:\Program Files\Google\Chrome\Application\chrome.exe", sort_order=20,
                      created_at=now))
    return len(STARTER_APPS) + 2
