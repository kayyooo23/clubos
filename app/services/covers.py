"""Обложки игр в меню гостя: вертикальный постер (PNG или JPEG, до 2 МБ). Два источника:
  steam   — картинка из каталога Steam по номеру игры (кнопка «Подтянуть обложки из Steam» в админке; запрос делает сервер, ПК в интернет за ними не ходят);
  program — иконка программы (вкладка «Программы»), которую прислал ПК: сервер ставит её сам, когда путь к exe совпал с найденной на ПК программой;
  manual  — загружена сотрудником вручную. Ручная обложка главнее: автоматика её никогда не заменяет.
Тип и размеры проверяются по самим байтам (services/images.py). version = отпечаток картинки: клиент на ПК скачивает заново, только если он сменился."""
import base64
import binascii
import hashlib
import json
import logging
import threading
import time
import urllib.error
import urllib.request
from collections.abc import Callable, Sequence
from datetime import datetime

from sqlalchemy import select
from sqlalchemy.orm import Session as DB, defer

from .. import config
from ..db import SessionLocal
from ..errors import ServiceError
from ..models import PC, AppCover, CatalogApp
from ..timeutil import utcnow
from . import catalog, images

log = logging.getLogger("clubos.covers")

STEAM_COVER_URL = "https://cdn.cloudflare.steamstatic.com/steam/apps/{id}/library_600x900.jpg"
COVER_MIN_SIDE = 48         # постер игры крупный, а иконка программы с ПК (ICO из exe) может быть 48 или 96 пикселей
STEAM_TIMEOUT = 8          # секунд на одну картинку
STEAM_BATCH_SECONDS = 60   # общий потолок на одну кнопку: остальное подтянется при следующем нажатии

Fetcher = Callable[[str], bytes | None]


def fetch_steam_cover(steam_id: str) -> bytes | None:
    """Скачивает постер игры из каталога Steam. None, если у игры его нет, сеть недоступна или файл не подходит. Номер игры только цифры:
    произвольный текст в адрес не подставляется."""
    if not steam_id.isdigit() or len(steam_id) > 12:
        return None
    request = urllib.request.Request(STEAM_COVER_URL.format(id=steam_id), headers={"User-Agent": "ClubOS/1.0"})
    try:
        with urllib.request.urlopen(request, timeout=STEAM_TIMEOUT) as response:
            data = response.read(images.MAX_BYTES + 1)
    except (urllib.error.URLError, TimeoutError, OSError):
        return None
    return data if 0 < len(data) <= images.MAX_BYTES else None


def get_cover(db: DB, app_id: int) -> AppCover | None:
    return db.get(AppCover, app_id)


def set_cover(db: DB, app: CatalogApp, data: bytes, source: str, now: datetime) -> AppCover:
    """Загружает или заменяет обложку. Не коммитит."""
    info = images.inspect_image(data, min_side=COVER_MIN_SIDE)
    version = hashlib.sha256(data).hexdigest()[:16]
    row = db.get(AppCover, app.id)
    if row is None:
        row = AppCover(app_id=app.id, content_type=info.content_type, width=info.width, height=info.height, version=version,
                       source=source, data=data, created_at=now)
        db.add(row)
    else:
        row.content_type, row.width, row.height, row.version, row.source, row.data, row.created_at = (
            info.content_type, info.width, info.height, version, source, data, now)
    db.flush()
    return row


def delete_cover(db: DB, app_id: int) -> None:
    row = db.get(AppCover, app_id)
    if row is None:
        raise ServiceError(404, "У игры нет обложки")
    db.delete(row)
    db.flush()


def meta_for_club(db: DB, club_id: int) -> dict[int, tuple[str, str]]:
    """Какие игры клуба с обложкой: app_id → (version, source). Без байтов: ими не нагружаем списки."""
    q = (select(AppCover).join(CatalogApp, CatalogApp.id == AppCover.app_id)
         .where(CatalogApp.club_id == club_id).options(defer(AppCover.data)))
    return {c.app_id: (c.version, c.source) for c in db.scalars(q)}


def fill_from_steam(db: DB, club_id: int, now: datetime, fetch: Fetcher | None = None) -> dict:
    """Подтягивает обложки играм клуба, которые запускаются через Steam и пока без обложки. Ручные и уже скачанные не трогает.
    Возвращает, сколько добавлено, у скольких Steam картинки не нашёл и сколько осталось на следующее нажатие. Не коммитит."""
    fetch = fetch or fetch_steam_cover          # имя берётся при вызове, поэтому тесты могут подменить скачивание
    have = meta_for_club(db, club_id)
    todo = [a for a in catalog.list_apps(db, club_id, active_only=False)
            if a.launch_kind == "steam" and a.tab == "games" and a.id not in have]
    added = failed = 0
    deadline = time.monotonic() + STEAM_BATCH_SECONDS
    for i, a in enumerate(todo):
        if time.monotonic() > deadline:
            return {"added": added, "not_found": failed, "left": len(todo) - i}
        data = fetch(a.launch_target)
        if data is None:
            failed += 1
            continue
        try:
            set_cover(db, a, data, "steam", now)
            added += 1
        except ServiceError:
            failed += 1             # Steam прислал не то, что мы принимаем: пропускаем эту игру
    return {"added": added, "not_found": failed, "left": 0}


_autofill_lock = threading.Lock()
_autofill_last: dict[int, float] = {}      # club_id -> когда в последний раз пробовали (time.monotonic)
_autofill_running: set[int] = set()        # клубы, у которых загрузка идёт прямо сейчас


def missing_steam_covers(apps: Sequence[CatalogApp], meta: dict[int, tuple[str, str]]) -> bool:
    """Есть ли среди этих записей игра из Steam без обложки (тогда стоит подтянуть постеры)."""
    return any(a.launch_kind == "steam" and a.tab == "games" and a.id not in meta for a in apps)


def autofill_steam(club_id: int, fetch: Fetcher | None = None, *, force: bool = False) -> dict | None:
    """Подтягивает постеры Steam играм клуба без обложки сама, без кнопки: запускается в фоне после ответа (BackgroundTasks), когда ПК запросил меню
    или сотрудник добавил игру из Steam. Не чаще раза в config.STEAM_AUTOCOVERS_EVERY_SECONDS на клуб (force: сразу, для только что добавленной игры)
    и не больше одной загрузки на клуб одновременно. Ручные и уже скачанные обложки не трогает (как и кнопка). Свой сеанс БД: запрос уже закончился.
    Сбой сети или Steam молча пропускается, игра остаётся с буквой до следующей попытки. Возвращает результат или None, если ничего не делалось."""
    now = time.monotonic()
    with _autofill_lock:
        if club_id in _autofill_running:
            return None
        if not force and now - _autofill_last.get(club_id, float("-inf")) < config.STEAM_AUTOCOVERS_EVERY_SECONDS:
            return None
        _autofill_running.add(club_id)
        _autofill_last[club_id] = now
    try:
        with SessionLocal() as db:
            result = fill_from_steam(db, club_id, utcnow(), fetch)
            db.commit()
        if result["added"]:
            log.info("Постеры Steam подтянуты автоматически: клуб %s, добавлено %s, не найдено %s", club_id, result["added"], result["not_found"])
        return result
    except Exception:                                   # фоновая задача не должна ронять сервер: покажем в журнале и попробуем в следующий раз
        log.exception("Не удалось подтянуть постеры Steam для клуба %s", club_id)
        return None
    finally:
        with _autofill_lock:
            _autofill_running.discard(club_id)


def program_icons(db: DB, club_id: int) -> dict[str, str]:
    """Иконки программ, найденных на ПК клуба: путь (строчными буквами) → PNG в base64. Если путь есть на нескольких ПК, берётся самый свежий отчёт."""
    icons: dict[str, str] = {}
    pcs = db.scalars(select(PC).where(PC.club_id == club_id, PC.programs_json.is_not(None)).order_by(PC.programs_at.desc())).all()
    for pc in pcs:
        try:
            programs = json.loads(pc.programs_json)
        except ValueError:
            continue
        for p in programs:
            icon, path = p.get("icon"), str(p.get("path", "")).lower()
            if icon and path and path not in icons:
                icons[path] = icon
    return icons


def fill_from_programs(db: DB, club_id: int, now: datetime) -> int:
    """Программам клуба (вкладка «Программы», запуск по пути к exe) без картинки ставит иконку, которую ПК прислал для того же пути.
    Ручные и уже стоящие картинки не трогает. Возвращает, сколько добавлено. Не коммитит."""
    have = meta_for_club(db, club_id)
    todo = [a for a in catalog.list_apps(db, club_id, active_only=False) if a.tab == "apps" and a.launch_kind == "exe" and a.id not in have]
    if not todo:
        return 0
    icons = program_icons(db, club_id)
    added = 0
    for a in todo:
        icon = icons.get(a.launch_target.lower())
        if icon is None:
            continue
        try:
            set_cover(db, a, base64.b64decode(icon, validate=True), "program", now)
            added += 1
        except (ServiceError, ValueError, binascii.Error):
            continue                # негодная иконка: запись остаётся с буквой
    return added
