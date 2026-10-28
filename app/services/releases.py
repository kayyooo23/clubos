"""Версии клиента для автообновления. Владелец платформы выпускает версию: подписывает пакет (zip со службой и оверлеем) своим закрытым ключом
(client/deploy/sign-release.ps1), заводит версию здесь и загружает пакет. Служба на каждом ПК раз в несколько минут спрашивает, нет ли новой
версии для неё (GET /api/agent/update), скачивает пакет и ставит его сама. Чего сервер не делает и не может: подделать обновление. Подпись проверяет
клиент по открытому ключу, вшитому в программу, поэтому взлом сервера или перехват связи не даёт запустить чужой код на ПК клубов.

Постепенная выдача: версия отдаётся, только если она включена (active), загружена и до ПК дошла очередь: club_ids (пусто: все клубы)
и rollout_percent (доля ПК; «номерок» ПК стабилен и разный для разных версий). Так версию сначала ставят на свой клуб или на пять процентов ПК."""
import hashlib
import os
import re
from datetime import datetime
from pathlib import Path

from sqlalchemy import func, select
from sqlalchemy.orm import Session as DB

from .. import config
from ..errors import ServiceError
from ..models import Club, ClientRelease, PC
from ..schemas import ReleaseIn, ReleasePatch, UpdateResultIn, VERSION_PATTERN
from . import audit

VERSION_RE = re.compile(VERSION_PATTERN)


def version_key(version: str) -> tuple[int, int, int, int]:
    """«1.10.0» больше «1.9.0»: сравниваем числами, а не строками. Недостающие числа считаются нулями."""
    parts = [int(x) for x in version.split(".")]
    return tuple((parts + [0, 0, 0, 0])[:4])  # type: ignore[return-value]


def valid_version(version: str | None) -> bool:
    return bool(version and VERSION_RE.match(version))


def release_dir() -> Path:
    return Path(config.RELEASES_DIR)


def club_list(r: ClientRelease) -> list[int]:
    return [int(x) for x in r.club_ids.split(",")] if r.club_ids else []


def release_out(r: ClientRelease, pcs: int = 0) -> dict:
    return {"id": r.id, "version": r.version, "notes": r.notes, "sha256": r.sha256, "size": r.size, "uploaded": r.file_name is not None,
            "uploaded_at": r.uploaded_at, "active": r.active, "rollout_percent": r.rollout_percent, "club_ids": club_list(r),
            "created_at": r.created_at, "pcs": pcs}


def offer_out(r: ClientRelease) -> dict:
    """Что узнаёт ПК: всё, что нужно для скачивания и проверки. Подпись и отпечаток клиент проверяет сам."""
    return {"id": r.id, "version": r.version, "sha256": r.sha256, "size": r.size, "signature": r.signature, "notes": r.notes,
            "url": f"/api/agent/update/package/{r.id}"}


def get(db: DB, release_id: int) -> ClientRelease:
    r = db.get(ClientRelease, release_id)
    if not r:
        raise ServiceError(404, "Версия не найдена")
    return r


def _clean_clubs(db: DB, club_ids: list[int] | None) -> str | None:
    ids = sorted(set(club_ids or []))
    for club_id in ids:
        if db.get(Club, club_id) is None:
            raise ServiceError(422, f"Клуб №{club_id} не найден")
    return ",".join(str(i) for i in ids) or None


def create(db: DB, data: ReleaseIn, now: datetime) -> ClientRelease:
    if db.scalar(select(ClientRelease.id).where(ClientRelease.version == data.version)):
        raise ServiceError(409, f"Версия {data.version} уже заведена")
    r = ClientRelease(version=data.version, notes=data.notes, sha256=data.sha256, size=data.size, signature=data.signature,
                      rollout_percent=data.rollout_percent, club_ids=_clean_clubs(db, data.club_ids), created_at=now)
    db.add(r)
    db.flush()
    return r


def part_path(r: ClientRelease) -> Path:
    return release_dir() / f"{r.id}-{r.version}.zip.part"


def final_path(r: ClientRelease) -> Path:
    return release_dir() / f"{r.id}-{r.version}.zip"


def check_can_upload(r: ClientRelease) -> None:
    if r.file_name:
        raise ServiceError(409, "Пакет этой версии уже загружен. Нужна другая версия: выпущенный пакет не подменяют")


def finish_upload(db: DB, r: ClientRelease, part: Path, sha256: str, size: int, now: datetime) -> None:
    """Загруженный файл должен совпасть с тем, что заявлено при создании версии (и что подписал владелец): иначе подпись всё равно не сойдётся у клиента."""
    if size != r.size or sha256 != r.sha256:
        part.unlink(missing_ok=True)
        raise ServiceError(422, "Загруженный файл не совпадает с заявленным (размер или отпечаток sha256). Проверьте, тот ли это пакет")
    final = final_path(r)
    os.replace(part, final)
    r.file_name, r.uploaded_at = final.name, now


def patch(db: DB, release_id: int, data: ReleasePatch) -> ClientRelease:
    r = get(db, release_id)
    fields = data.model_fields_set
    if data.active and not r.file_name:
        raise ServiceError(409, "Пакет ещё не загружен: включать версию рано")
    if "notes" in fields:
        r.notes = data.notes
    if "active" in fields and data.active is not None:
        r.active = data.active
    if "rollout_percent" in fields and data.rollout_percent is not None:
        r.rollout_percent = data.rollout_percent
    if "club_ids" in fields:
        r.club_ids = _clean_clubs(db, data.club_ids)
    return r


def delete(db: DB, release_id: int) -> None:
    r = get(db, release_id)
    if r.active:
        raise ServiceError(409, "Версия включена: сначала выключите её")
    final_path(r).unlink(missing_ok=True)
    part_path(r).unlink(missing_ok=True)
    db.delete(r)


def list_out(db: DB) -> list[dict]:
    counts = dict(db.execute(select(PC.client_version, func.count()).where(PC.client_version.is_not(None)).group_by(PC.client_version)).all())
    rows = db.scalars(select(ClientRelease)).all()
    rows.sort(key=lambda r: version_key(r.version), reverse=True)
    return [release_out(r, counts.get(r.version, 0)) for r in rows]


# ---------- кому отдавать ----------
def bucket(release_id: int, pc_id: int) -> int:
    """Стабильный «номерок» ПК от 0 до 99 для этой версии: версия с rollout_percent=N достаётся ПК с номерком меньше N."""
    digest = hashlib.sha256(f"{config.UPDATE_ROLLOUT_SALT}:{release_id}:{pc_id}".encode()).digest()
    return int.from_bytes(digest[:4], "big") % 100


def eligible(r: ClientRelease, pc: PC) -> bool:
    if not (r.active and r.file_name):
        return False
    clubs = club_list(r)
    if clubs and pc.club_id not in clubs:
        return False
    return bucket(r.id, pc.id) < r.rollout_percent


def offer_for(db: DB, pc: PC, current: str) -> ClientRelease | None:
    """Самая новая из доступных этому ПК версий, которая новее текущей. Откат назад сервер не предлагает: «старую» версию выпускают заново под новым номером."""
    have = version_key(current) if valid_version(current) else (0, 0, 0, 0)
    best = None
    for r in db.scalars(select(ClientRelease).where(ClientRelease.active.is_(True), ClientRelease.file_name.is_not(None))):
        if version_key(r.version) > have and eligible(r, pc) and (best is None or version_key(r.version) > version_key(best.version)):
            best = r
    return best


def offerable(db: DB, pc: PC, release_id: int) -> ClientRelease:
    r = db.get(ClientRelease, release_id)
    if not r or not eligible(r, pc):
        raise ServiceError(404, "Версия недоступна")
    return r


# ---------- что присылает ПК ----------
def record_check(pc: PC, version: str, now: datetime) -> None:
    if pc.client_version != version:
        pc.client_version = version
    pc.client_version_at = now


def record_result(db: DB, pc: PC, data: UpdateResultIn, now: datetime) -> None:
    pc.update_status, pc.update_message, pc.update_at = data.status, data.message, now
    if data.status == "ok":
        pc.client_version, pc.client_version_at = data.version, now
    audit.record(db, pc.club_id, f"client_update_{data.status}", now, pc_id=pc.id, details=f"version={data.version}")
