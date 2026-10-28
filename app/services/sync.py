"""Синхронизация игр между ПК клуба. Администратор ставит или обновляет игру один раз на эталонном ПК, а система копирует её на остальные.
Файлы идут по локальной сети напрямую между ПК: облачный сервер их не видит и не пропускает через себя, он только координирует (кто эталон, что копировать,
кто на каком этапе) и отдаёт участникам секрет задания. Что именно копируется и как проверяется, решает клиент (client/ClubOS.Service/Sync):
список файлов с хешами блоков считает эталон, sha256 этого списка сообщает серверу, а получатели сверяют его со списком, скачанным у эталона, и проверяют каждый блок.

Как участники находят друг друга: каждый ПК, который уже может раздавать файлы, сообщает свой адрес в локальной сети (serve_url, только частные адреса).
Сервер отдаёт получателю список таких участников (peers), и получатель берёт файлы не только у эталона, а у любого, у кого они уже есть: тридцать ПК не задавят эталон.
Один ПК участвует не больше чем в одном идущем задании. Задание, которое идёт дольше JOB_TTL_HOURS, отменяется само."""
import ipaddress
import json
import re
import secrets
from datetime import datetime, timedelta

from sqlalchemy import select
from sqlalchemy.orm import Session as DB

from .. import config
from ..errors import ServiceError
from ..models import Club, PC, SyncJob, SyncTarget
from ..schemas import GamesIn, SyncJobIn, SyncManifestIn, SyncProgressIn
from . import audit, logins, sessions

JOB_TTL_HOURS = 48
ACTIVE = ("pending", "scanning", "waiting", "syncing")        # участник ещё работает
LIVE = ACTIVE + ("ready",)                                    # участник входит в задание (готовый продолжает раздавать файлы)
TERMINAL = ("ready", "failed", "cancelled")
MAX_PATHS = 20
MAX_PEERS = 32
SOURCE_STATUSES = ("scanning", "ready", "failed")
TARGET_STATUSES = ("waiting", "syncing", "ready", "failed")
_BAD_SEGMENT = re.compile(r'[<>"|?*\x00-\x1f]')


def clean_path(raw: str) -> str:
    """Путь внутри игровой папки: через «/», без «..», без диска, без абсолютных путей и недопустимых для Windows знаков. Клиент проверяет всё это ещё раз сам."""
    path = (raw or "").strip().replace("\\", "/")
    if not path or len(path) > 260:
        raise ServiceError(422, "Путь должен быть непустым и не длиннее 260 знаков")
    if path.startswith("/") or ":" in path:
        raise ServiceError(422, f"Путь «{path[:60]}» должен быть относительным, внутри игровой папки")
    parts = path.rstrip("/").split("/")
    for part in parts:
        if part in ("", ".", "..") or _BAD_SEGMENT.search(part) or part != part.rstrip(" ."):
            raise ServiceError(422, f"В пути «{path[:60]}» недопустимая часть «{part[:30]}»")
    return "/".join(parts)


def clean_paths(paths: list[str]) -> list[str]:
    """Без повторов; путь, уже входящий в другой выбранный (папка и её подпапка), отбрасывается."""
    first_spelling: dict[str, str] = {}
    for p in paths:
        cleaned_path = clean_path(p)
        first_spelling.setdefault(cleaned_path.lower(), cleaned_path)       # Windows не различает регистр: остаётся первое написание
    cleaned = sorted(first_spelling.values(), key=lambda p: (p.count("/"), p.lower()))
    kept: list[str] = []
    for p in cleaned:
        if not any(p.lower() == k.lower() or p.lower().startswith(k.lower() + "/") for k in kept):
            kept.append(p)
    return kept


def _online(pc: PC, now: datetime) -> bool:
    return bool(pc.last_seen and (now - pc.last_seen).total_seconds() < config.PC_ONLINE_SECONDS)


# ---------- список игр на ПК ----------
def set_games(db: DB, pc: PC, data: GamesIn, now: datetime) -> int:
    pc.games_json = json.dumps({"root": data.root, "entries": data.entries}, ensure_ascii=False)
    pc.games_at = now
    return len(data.entries)


def games_scan_requested(pc: PC) -> bool:
    return pc.games_wanted_at is not None and (pc.games_at is None or pc.games_wanted_at > pc.games_at)


def request_games_scan(db: DB, club_id: int, pc_id: int, now: datetime) -> PC:
    pc = sessions.get_pc(db, club_id, pc_id)
    pc.games_wanted_at = now
    db.commit()
    return pc


def games_view(pc: PC, now: datetime) -> dict:
    try:
        data = json.loads(pc.games_json) if pc.games_json else {}
    except ValueError:
        data = {}
    return {"pc": {"id": pc.id, "name": pc.name, "online": _online(pc, now)}, "reported_at": pc.games_at, "pending": games_scan_requested(pc),
            "root": data.get("root"), "entries": data.get("entries", [])}


# ---------- задания ----------
def expire_old(db: DB, club_id: int, now: datetime) -> None:
    for job in db.scalars(select(SyncJob).where(SyncJob.club_id == club_id, SyncJob.status == "running", SyncJob.created_at <= now - timedelta(hours=JOB_TTL_HOURS))):
        _cancel(db, job, now, "Истёк срок задания")


def _busy_names(db: DB, pc_ids: list[int]) -> list[str]:
    rows = db.scalars(select(PC.name).join(SyncTarget, SyncTarget.pc_id == PC.id).join(SyncJob, SyncJob.id == SyncTarget.job_id)
                      .where(SyncTarget.pc_id.in_(pc_ids), SyncJob.status == "running", SyncTarget.status.in_(LIVE)).distinct())
    return sorted(rows)


def create_job(db: DB, club: Club, staff, data: SyncJobIn, now: datetime) -> tuple[SyncJob, list[str]]:
    """Создаёт задание. Возвращает (задание, названия ПК без связи, которые по умолчанию пропущены). Без явного списка получателей копируется на ПК, которые сейчас
    на связи: выключенные ПК иначе держали бы задание открытым до их включения. Их можно указать явно, и они начнут копирование, когда включатся."""
    expire_old(db, club.id, now)
    skipped: list[str] = []
    source = sessions.get_pc(db, club.id, data.source_pc_id)
    if not _online(source, now):
        raise ServiceError(409, f"Эталонный ПК {source.name} не на связи: игры с него сейчас не скопировать")
    if sessions.active_session(db, club.id, source.id) or source.id in logins.guest_cabinet_phones(db, club, now):
        # гость за ПК: Steam или сама игра могут менять файлы прямо во время копирования, и получатели получили бы «рваную» игру
        raise ServiceError(409, f"За эталонным ПК {source.name} сидит гость: файлы игры могут меняться во время копирования. Выберите свободный ПК или дождитесь, когда гость уйдёт")
    paths = clean_paths(data.paths)
    if len(paths) > MAX_PATHS:
        raise ServiceError(422, f"Не больше {MAX_PATHS} папок за раз")
    if data.target_pc_ids:
        wanted = list(dict.fromkeys(data.target_pc_ids))
        targets = [sessions.get_pc(db, club.id, pc_id) for pc_id in wanted]
        if any(t.id == source.id for t in targets):
            raise ServiceError(422, "Эталонный ПК не может быть и получателем")
    else:
        others = list(db.scalars(select(PC).where(PC.club_id == club.id, PC.id != source.id).order_by(PC.id)))
        targets = [t for t in others if _online(t, now)]
        skipped = [t.name for t in others if not _online(t, now)]
    if not targets:
        raise ServiceError(422, "Нет ПК на связи, на которые копировать" if skipped else "Нет ПК, на которые копировать")
    ids = [source.id] + [t.id for t in targets]
    db.execute(select(PC.id).where(PC.id.in_(ids)).order_by(PC.id).with_for_update())   # два задания на одни ПК не создаются одновременно (на PostgreSQL; порядок по id без взаимных блокировок)
    busy = _busy_names(db, ids)
    if busy:
        raise ServiceError(409, f"ПК уже участвуют в другой синхронизации: {', '.join(busy[:8])}")
    job = SyncJob(club_id=club.id, source_pc_id=source.id, paths=json.dumps(paths, ensure_ascii=False), delete_extra=data.delete_extra,
                  token=secrets.token_urlsafe(24), created_by=staff.id if staff else None, created_at=now)
    db.add(job)
    db.flush()
    db.add(SyncTarget(job_id=job.id, pc_id=source.id, role="source", updated_at=now))
    for t in targets:
        db.add(SyncTarget(job_id=job.id, pc_id=t.id, role="target", updated_at=now))
    audit.record(db, club.id, "sync_start", now, actor=staff, pc_id=source.id, details=f"job={job.id} paths={len(paths)} targets={len(targets)}")
    return job, skipped


def get_job(db: DB, club_id: int, job_id: int) -> SyncJob:
    job = db.scalar(select(SyncJob).where(SyncJob.id == job_id, SyncJob.club_id == club_id))
    if not job:
        raise ServiceError(404, "Задание не найдено")
    return job


def _rows(db: DB, job_id: int) -> list[SyncTarget]:
    return list(db.scalars(select(SyncTarget).where(SyncTarget.job_id == job_id).order_by(SyncTarget.role.desc(), SyncTarget.pc_id)))


def _cancel(db: DB, job: SyncJob, now: datetime, message: str) -> None:
    job.status, job.finished_at = "cancelled", now
    for row in _rows(db, job.id):
        if row.status in ACTIVE:
            row.status, row.message, row.finished_at, row.updated_at = "cancelled", message, now, now


def cancel_job(db: DB, club: Club, staff, job_id: int, now: datetime) -> SyncJob:
    job = get_job(db, club.id, job_id)
    if job.status != "running":
        raise ServiceError(409, "Задание уже закрыто")
    _cancel(db, job, now, "Отменено администратором")
    audit.record(db, club.id, "sync_cancel", now, actor=staff, pc_id=job.source_pc_id, details=f"job={job.id}")
    return job


def _maybe_finish(db: DB, job: SyncJob, now: datetime) -> None:
    rows = _rows(db, job.id)
    source = next((r for r in rows if r.role == "source"), None)
    if source is not None and source.status == "failed":
        for row in rows:                                    # без списка файлов получателям копировать нечего
            if row.status in ACTIVE:
                row.status, row.message, row.finished_at, row.updated_at = "failed", "Эталонный ПК не смог подготовить файлы", now, now
    if not any(r.status in ACTIVE for r in rows):
        job.status, job.finished_at = "done", now


# ---------- что видит ПК ----------
def peers_for(db: DB, job: SyncJob, exclude_pc_id: int) -> list[dict]:
    rows = db.scalars(select(SyncTarget).where(SyncTarget.job_id == job.id, SyncTarget.pc_id != exclude_pc_id, SyncTarget.serve_url.is_not(None),
                                               SyncTarget.status.in_(("syncing", "ready"))).order_by(SyncTarget.pc_id).limit(MAX_PEERS))
    return [{"pc_id": r.pc_id, "url": r.serve_url, "role": r.role} for r in rows]


def assignments_for(db: DB, pc: PC) -> list[dict]:
    """Задания, в которых этот ПК сейчас участвует (идут и он не закончил неудачей). Приходят в ответе /api/agent/state, пока есть что делать."""
    out = []
    pairs = db.execute(select(SyncTarget, SyncJob).join(SyncJob, SyncJob.id == SyncTarget.job_id)
                       .where(SyncTarget.pc_id == pc.id, SyncJob.status == "running", SyncTarget.status.in_(LIVE)).order_by(SyncJob.id)).all()
    for row, job in pairs:
        item = {"id": job.id, "role": row.role, "status": row.status, "token": job.token, "paths": json.loads(job.paths), "delete_extra": job.delete_extra,
                "manifest_sha256": job.manifest_sha256}
        if row.role == "target" and job.manifest_sha256:
            item["peers"] = peers_for(db, job, pc.id)
        out.append(item)
    return out


def register_manifest(db: DB, pc: PC, job_id: int, data: SyncManifestIn, now: datetime) -> SyncJob:
    job = db.get(SyncJob, job_id)
    row = db.scalar(select(SyncTarget).where(SyncTarget.job_id == job_id, SyncTarget.pc_id == pc.id, SyncTarget.role == "source")) if job else None
    if not job or job.club_id != pc.club_id or row is None:
        raise ServiceError(404, "Задание не найдено или этот ПК не эталон")
    if job.status != "running":
        raise ServiceError(409, "Задание уже закрыто")
    if job.manifest_sha256 is not None and job.manifest_sha256 != data.sha256:
        raise ServiceError(409, "Список файлов этого задания уже зарегистрирован другой")
    job.manifest_sha256, job.manifest_files, job.manifest_bytes = data.sha256, data.files, data.bytes
    row.bytes_total, row.files_total, row.updated_at = data.bytes, data.files, now
    return job


def _private_url(url: str) -> bool:
    try:
        host = url[len("http://"):].rsplit(":", 1)[0]
        ip = ipaddress.ip_address(host)
        port = int(url.rsplit(":", 1)[1])
    except ValueError:
        return False
    return ip.is_private and 1 <= port <= 65535


def report_progress(db: DB, pc: PC, job_id: int, data: SyncProgressIn, now: datetime) -> SyncTarget:
    job = db.get(SyncJob, job_id)
    row = db.scalar(select(SyncTarget).where(SyncTarget.job_id == job_id, SyncTarget.pc_id == pc.id)) if job else None
    if not job or job.club_id != pc.club_id or row is None:
        raise ServiceError(404, "Задание не найдено")
    if job.status != "running" or row.status == "cancelled":
        raise ServiceError(409, "Задание уже закрыто")
    allowed = SOURCE_STATUSES if row.role == "source" else TARGET_STATUSES
    if data.status not in allowed:
        raise ServiceError(422, f"Для этого ПК нельзя сообщить состояние «{data.status}»")
    if data.serve_url is not None and not _private_url(data.serve_url):
        raise ServiceError(422, "Адрес раздачи должен быть адресом в локальной сети")
    if data.status == "ready" and job.manifest_sha256 is None:
        raise ServiceError(409, "Список файлов задания ещё не зарегистрирован")
    row.status, row.message, row.updated_at = data.status, data.message, now
    row.bytes_done, row.bytes_total, row.bytes_fetched, row.bytes_from_peers = data.bytes_done, data.bytes_total, data.bytes_fetched, data.bytes_from_peers
    row.files_done, row.files_total = data.files_done, data.files_total
    if data.serve_url is not None:
        row.serve_url = data.serve_url
    if data.status in ("ready", "failed"):
        row.finished_at = now
    _maybe_finish(db, job, now)
    return row


# ---------- что видит администратор ----------
def job_out(db: DB, job: SyncJob) -> dict:
    names = {p.id: p.name for p in db.scalars(select(PC).where(PC.club_id == job.club_id))}
    rows = _rows(db, job.id)
    targets = []
    for r in rows:
        percent = int(min(100, r.bytes_done * 100 // r.bytes_total)) if r.bytes_total else (100 if r.status == "ready" else 0)
        targets.append({"pc_id": r.pc_id, "pc_name": names.get(r.pc_id, "?"), "role": r.role, "status": r.status, "percent": percent,
                        "bytes_done": r.bytes_done, "bytes_total": r.bytes_total, "bytes_fetched": r.bytes_fetched, "bytes_from_peers": r.bytes_from_peers,
                        "files_done": r.files_done, "files_total": r.files_total, "message": r.message, "updated_at": r.updated_at})
    receivers = [t for t in targets if t["role"] == "target"]
    return {"id": job.id, "status": job.status, "source_pc_id": job.source_pc_id, "source_name": names.get(job.source_pc_id, "?"), "paths": json.loads(job.paths),
            "delete_extra": job.delete_extra, "created_at": job.created_at, "finished_at": job.finished_at, "manifest_files": job.manifest_files,
            "manifest_bytes": job.manifest_bytes, "targets": targets,
            "summary": {"total": len(receivers), "ready": sum(t["status"] == "ready" for t in receivers), "failed": sum(t["status"] == "failed" for t in receivers),
                        "syncing": sum(t["status"] == "syncing" for t in receivers), "fetched": sum(t["bytes_fetched"] for t in receivers),
                        "from_peers": sum(t["bytes_from_peers"] for t in receivers)}}


def list_jobs(db: DB, club_id: int, now: datetime, limit: int = 20) -> list[dict]:
    expire_old(db, club_id, now)
    jobs = db.scalars(select(SyncJob).where(SyncJob.club_id == club_id).order_by(SyncJob.id.desc()).limit(limit))
    return [job_out(db, j) for j in jobs]
