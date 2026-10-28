"""Управление клубом — для сотрудника (токен из /api/admin/auth/login). Всё строго в пределах его клуба."""
from datetime import datetime, timedelta, timezone

from fastapi import APIRouter, BackgroundTasks, Depends, Request
from fastapi.responses import Response
from sqlalchemy import func, select
from sqlalchemy.orm import Session as DB

from .. import config
from ..db import get_db
from ..deps import StaffContext, staff_context
from ..errors import ServiceError, commit_or_409
from ..models import Account, AuditLog, Club, Order, PC, Session, Tariff, WalletTransaction
from ..phone import normalize_phone
from ..schemas import (AdjustIn, AppIn, AppPatch, BookingIn, BookingPatch, BulkCommandIn, ClubAdsToggleIn, CommandIn, CopyTariffsIn, EnrollCodeIn, LayoutIn,
                       MoneyIn, NoticeIn, OrderStatusIn, PCIn, PCPatch, ProductIn, ProductPatch, SessionIn, SyncJobIn, TariffIn, ZoneIn, ZonePatch)
from ..security import hash_secret, new_secret
from ..services import (accounts, ads, audit, billing, bookings, cashier, catalog, commands, covers, enroll, fleet, images, logins, notices, sessions, shop,
                        sync, zones)
from ..timeutil import utcnow

router = APIRouter(prefix="/api/admin", tags=["админка"])


def _ip(request: Request) -> str | None:
    return request.client.host if request.client else None


def _guest(db: DB, club_id: int, account_id: int) -> Account:
    acc = accounts.get_account(db, club_id, account_id)
    if acc.role != "guest":
        raise ServiceError(404, "Аккаунт не найден")
    return acc


# ---------- ПК ----------
@router.post("/pcs")
def create_pc(data: PCIn, ctx: StaffContext = Depends(staff_context), db: DB = Depends(get_db)):
    """Ключ ПК показывается ОДИН раз — его нужно прописать в клиент на этом ПК. В базе остаётся только хеш."""
    if db.scalar(select(PC).where(PC.club_id == ctx.club.id, PC.name == data.name)):
        raise ServiceError(409, "ПК с таким именем уже есть")
    key = new_secret()
    pc = PC(club_id=ctx.club.id, name=data.name, key_hash=hash_secret(key))
    if data.zone_id is not None:
        zones.assign_pc(db, ctx.club.id, pc, data.zone_id)     # ПК в зале-шаблоне: название зала на карте и тарифы берутся у зала
    db.add(pc)
    commit_or_409(db, "ПК с таким именем уже есть")
    return {"id": pc.id, "name": pc.name, "key": key, "zone_id": pc.zone_id, "zone": pc.zone}


@router.post("/pcs/{pc_id}/rotate-key")
def rotate_pc_key(pc_id: int, request: Request, ctx: StaffContext = Depends(staff_context), db: DB = Depends(get_db)):
    """Если ключ мог утечь: выдаёт новый, старый перестаёт работать сразу."""
    pc = sessions.get_pc(db, ctx.club.id, pc_id)
    key = new_secret()
    pc.key_hash = hash_secret(key)
    if pc.enrolled_at is None:
        # ПК создан вручную: новый ключ администратор вписывает сам, в том числе на другом железе, поэтому привязку к прежнему снимаем.
        # ПК, подключённый по коду, остаётся привязан: служба сама получит новый ключ, повторно подключившись с того же компьютера.
        pc.machine_id, pc.clone_alert_at = None, None
    audit.record(db, ctx.club.id, "pc_key_rotated", utcnow(), actor=ctx.account, pc_id=pc.id, ip=_ip(request))
    db.commit()
    return {"id": pc.id, "name": pc.name, "key": key}


@router.post("/pcs/{pc_id}/lock")
def lock_pc(pc_id: int, request: Request, ctx: StaffContext = Depends(staff_context), db: DB = Depends(get_db)):
    """Принудительно вернуть ПК на экран блокировки: завершает вход (рабочий стол сотрудника / кабинет гостя).
    Идущую игровую сессию так не остановить — для этого есть POST /sessions/{id}/stop."""
    now = utcnow()
    pc = sessions.get_pc(db, ctx.club.id, pc_id)
    sessions.close_expired(db, ctx.club.id, now)
    if sessions.active_session(db, ctx.club.id, pc.id):
        raise ServiceError(409, "На ПК идёт сессия гостя. Сначала остановите её")
    logins.end_pc_logins(db, pc.id, now)
    audit.record(db, ctx.club.id, "pc_force_lock", now, actor=ctx.account, pc_id=pc.id, ip=_ip(request))
    db.commit()
    return {"ok": True}


@router.get("/pcs")
def list_pcs(ctx: StaffContext = Depends(staff_context), db: DB = Depends(get_db)):
    now = utcnow()
    sessions.close_expired(db, ctx.club.id, now)
    active = sessions.active_sessions_by_pc(db, ctx.club.id)
    desktops = logins.staff_desktop_pc_ids(db, ctx.club.id, now)
    cabinets = logins.guest_cabinet_phones(db, ctx.club, now)
    phones = {acc_id: phone for acc_id, phone in db.execute(select(Account.id, Account.phone).where(
        Account.id.in_([s.account_id for s in active.values()])))} if active else {}
    result = []
    upcoming = bookings.next_for_each_pc(db, ctx.club.id, now)
    last_notices = notices.latest_by_pc(db, ctx.club.id, now)
    last_commands = commands.latest_by_pc(db, ctx.club.id, now)
    for pc in db.scalars(select(PC).where(PC.club_id == ctx.club.id).order_by(PC.id)):
        s = active.get(pc.id)
        booking = upcoming.get(pc.id)
        notice = last_notices.get(pc.id)
        command = last_commands.get(pc.id)
        online = bool(pc.last_seen and (now - pc.last_seen).total_seconds() < config.PC_ONLINE_SECONDS)
        mode = "staff_desktop" if pc.id in desktops else "guest_session" if s else "locked"
        result.append({"id": pc.id, "name": pc.name, "online": online, "mode": mode,
                       "guest_phone": phones.get(s.account_id) if s else None,
                       "cabinet_phone": cabinets.get(pc.id) if not s and pc.id not in desktops else None,   # гость сидит за ПК, но сессию ещё не начал
                       "session":sessions.session_out(s, now) if s else None,
                       "zone": pc.zone, "zone_id": pc.zone_id, "pos_x": pc.pos_x, "pos_y": pc.pos_y, "note": pc.note,
                       "maintenance": pc.maintenance, "hw": fleet.hw_view(pc), "hw_changed": pc.hw_changed_at is not None,
                       "booking": bookings.booking_out(booking, now) if booking else None,
                       "last_notice": notices.notice_out(notice) if notice else None,
                       "enrolled_at": pc.enrolled_at, "clone_alert_at": pc.clone_alert_at,
                       "client_version": pc.client_version, "update_status": pc.update_status, "update_message": pc.update_message, "update_at": pc.update_at,
                       "last_command": commands.command_out(command) if command else None})
    return result


# ---------- подключение ПК по коду ----------
@router.post("/enroll-codes")
def create_enroll_code(data: EnrollCodeIn, request: Request, ctx: StaffContext = Depends(staff_context), db: DB = Depends(get_db)):
    """Код подключения ПК: его вводят при установке клиента, и ПК сам подключается к клубу и получает свой ключ. Сам код виден только в этом ответе."""
    now = utcnow()
    row, code = enroll.create_code(db, ctx.club, ctx.account, data, now)
    audit.record(db, ctx.club.id, "enroll_code_create", now, actor=ctx.account, details=f"code={row.id}", ip=_ip(request))
    db.commit()
    return {**enroll.code_out(row, now), "code": code}


@router.get("/enroll-codes")
def list_enroll_codes(ctx: StaffContext = Depends(staff_context), db: DB = Depends(get_db)):
    return enroll.list_codes(db, ctx.club.id, utcnow())


@router.delete("/enroll-codes/{code_id}")
def revoke_enroll_code(code_id: int, request: Request, ctx: StaffContext = Depends(staff_context), db: DB = Depends(get_db)):
    """Отозвать код: новые ПК по нему больше не подключатся (уже подключённые работают как работали)."""
    now = utcnow()
    c = enroll.revoke_code(db, ctx.club.id, code_id, now)
    audit.record(db, ctx.club.id, "enroll_code_revoke", now, actor=ctx.account, details=f"code={c.id}", ip=_ip(request))
    db.commit()
    return enroll.code_out(c, now)


# ---------- команды ПК ----------
def _online(pc: PC, now) -> bool:
    return bool(pc.last_seen and (now - pc.last_seen).total_seconds() < config.PC_ONLINE_SECONDS)


@router.post("/pcs/{pc_id}/commands")
def send_command(pc_id: int, data: CommandIn, request: Request, ctx: StaffContext = Depends(staff_context), db: DB = Depends(get_db)):
    """Перезагрузить или выключить ПК. ПК выполнит команду с ближайшим опросом (раз в пару секунд); если он не на связи, команда ждёт десять минут.
    На ПК идёт сессия гостя: без force (подтверждения) сервер откажет."""
    now = utcnow()
    cmd, created = commands.create(db, ctx.club.id, ctx.account.id, pc_id, data.kind, data.force, now)
    if created:
        audit.record(db, ctx.club.id, "pc_command", now, actor=ctx.account, pc_id=pc_id, details=f"cmd={cmd.id} kind={cmd.kind} force={int(data.force)}", ip=_ip(request))
    db.commit()
    return {**commands.command_out(cmd), "created": created, "online": _online(db.get(PC, pc_id), now)}


@router.post("/commands")
def send_command_to_many(data: BulkCommandIn, request: Request, ctx: StaffContext = Depends(staff_context), db: DB = Depends(get_db)):
    """Одна команда сразу нескольким ПК (например, перезагрузить все свободные). По каждому ПК свой результат: где-то можно отказать (идёт сессия), остальные выполнятся."""
    now = utcnow()
    results = []
    for pc_id in dict.fromkeys(data.pc_ids):             # без повторов, в том порядке, как пришли
        try:
            cmd, created = commands.create(db, ctx.club.id, ctx.account.id, pc_id, data.kind, data.force, now)
            if created:
                audit.record(db, ctx.club.id, "pc_command", now, actor=ctx.account, pc_id=pc_id, details=f"cmd={cmd.id} kind={cmd.kind} force={int(data.force)}", ip=_ip(request))
            results.append({"pc_id": pc_id, "ok": True, "created": created, "command_id": cmd.id})
        except ServiceError as e:
            results.append({"pc_id": pc_id, "ok": False, "error": e.detail})
    db.commit()
    return {"results": results, "queued": sum(1 for r in results if r["ok"] and r["created"]), "refused": sum(1 for r in results if not r["ok"])}


@router.get("/pcs/{pc_id}/commands")
def pc_commands(pc_id: int, ctx: StaffContext = Depends(staff_context), db: DB = Depends(get_db)):
    sessions.get_pc(db, ctx.club.id, pc_id)
    commands.expire_old(db, ctx.club.id, utcnow())
    db.commit()
    return [commands.command_out(c) for c in commands.recent_for_pc(db, ctx.club.id, pc_id)]


@router.post("/commands/{command_id}/cancel")
def cancel_command(command_id: int, request: Request, ctx: StaffContext = Depends(staff_context), db: DB = Depends(get_db)):
    now = utcnow()
    c = commands.cancel(db, ctx.club.id, command_id, now)
    audit.record(db, ctx.club.id, "pc_command_cancel", now, actor=ctx.account, pc_id=c.pc_id, details=f"cmd={c.id}", ip=_ip(request))
    db.commit()
    return commands.command_out(c)


@router.post("/pcs/{pc_id}/notice")
def send_notice(pc_id: int, data: NoticeIn, request: Request, ctx: StaffContext = Depends(staff_context), db: DB = Depends(get_db)):
    """Показать гостю на экране этого ПК сообщение администратора (любой текст до 300 знаков). Показывается один раз поверх всех окон
    со звуком; если ПК сейчас не на связи, сообщение дойдёт, только если связь вернётся в течение двух минут."""
    now = utcnow()
    n = notices.send(db, ctx.club, ctx.account, pc_id, data.text, now)
    audit.record(db, ctx.club.id, "pc_notice", now, actor=ctx.account, pc_id=n.pc_id, details=f"notice={n.id}", ip=_ip(request))
    db.commit()
    pc = db.get(PC, n.pc_id)
    online = bool(pc.last_seen and (now - pc.last_seen).total_seconds() < config.PC_ONLINE_SECONDS)
    return {**notices.notice_out(n), "online": online}


@router.patch("/pcs/{pc_id}")
def update_pc(pc_id: int, data: PCPatch, request: Request, ctx: StaffContext = Depends(staff_context), db: DB = Depends(get_db)):
    """Имя, зал, клетка на карте, заметка, «на обслуживании». На обслуживании гостей на ПК не пускаем."""
    now = utcnow()
    pc = fleet.update_pc(db, ctx.club.id, pc_id, data, now)
    action = "pc_maintenance_on" if data.maintenance else "pc_maintenance_off" if data.maintenance is False else "pc_update"
    audit.record(db, ctx.club.id, action, now, actor=ctx.account, pc_id=pc.id, ip=_ip(request))
    db.commit()
    return {"id": pc.id, "name": pc.name, "zone": pc.zone, "pos_x": pc.pos_x, "pos_y": pc.pos_y, "note": pc.note,
            "maintenance": pc.maintenance}


@router.post("/pcs/layout")
def save_pc_layout(data: LayoutIn, request: Request, ctx: StaffContext = Depends(staff_context), db: DB = Depends(get_db)):
    """Расстановка ПК на карте целиком (после перетаскивания)."""
    count = fleet.save_layout(db, ctx.club.id, data)
    audit.record(db, ctx.club.id, "pc_layout", utcnow(), actor=ctx.account, details=f"count={count}", ip=_ip(request))
    db.commit()
    return {"saved": count}


@router.get("/pcs/{pc_id}/programs")
def pc_programs(pc_id: int, icons: bool = True, ctx: StaffContext = Depends(staff_context), db: DB = Depends(get_db)):
    """Программы, найденные на этом ПК: из них администратор выбирает, что добавить в меню гостя (путь тогда точно существует на ПК клуба).
    icons=false: без иконок (лёгкий ответ для частых опросов, пока ждём обновления списка)."""
    return fleet.programs_view(sessions.get_pc(db, ctx.club.id, pc_id), utcnow(), icons=icons)


@router.post("/pcs/{pc_id}/programs/rescan")
def pc_programs_rescan(pc_id: int, ctx: StaffContext = Depends(staff_context), db: DB = Depends(get_db)):
    """Попросить службу на ПК пересобрать список (поставили новую программу). Служба узнает об этом за несколько секунд."""
    now = utcnow()
    pc = fleet.request_programs_scan(db, ctx.club.id, pc_id, now)
    return fleet.programs_view(pc, now, icons=False)


# ---------- синхронизация игр ----------
@router.get("/pcs/{pc_id}/games")
def pc_games(pc_id: int, ctx: StaffContext = Depends(staff_context), db: DB = Depends(get_db)):
    """Игры в игровой папке этого ПК: из них администратор выбирает, что копировать на остальные ПК."""
    return sync.games_view(sessions.get_pc(db, ctx.club.id, pc_id), utcnow())


@router.post("/pcs/{pc_id}/games/rescan")
def pc_games_rescan(pc_id: int, ctx: StaffContext = Depends(staff_context), db: DB = Depends(get_db)):
    """Попросить службу на ПК пересобрать список игр (поставили или обновили игру). Служба узнает об этом за несколько секунд."""
    now = utcnow()
    pc = sync.request_games_scan(db, ctx.club.id, pc_id, now)
    return sync.games_view(pc, now)


@router.post("/sync/jobs")
def sync_start(data: SyncJobIn, ctx: StaffContext = Depends(staff_context), db: DB = Depends(get_db)):
    """Копировать выбранные игры с эталонного ПК на другие ПК клуба. Файлы идут по локальной сети между ПК, ПК копируют, когда свободны (без гостя)."""
    job, skipped = sync.create_job(db, ctx.club, ctx.account, data, utcnow())
    db.commit()
    return {**sync.job_out(db, job), "skipped_offline": skipped}                    # ПК без связи по умолчанию пропущены: их можно выбрать вручную


@router.get("/sync/jobs")
def sync_jobs(ctx: StaffContext = Depends(staff_context), db: DB = Depends(get_db)):
    now = utcnow()
    out = sync.list_jobs(db, ctx.club.id, now)
    db.commit()                                                                   # истёкшие задания закрываются при просмотре
    return out


@router.get("/sync/jobs/{job_id}")
def sync_job(job_id: int, ctx: StaffContext = Depends(staff_context), db: DB = Depends(get_db)):
    return sync.job_out(db, sync.get_job(db, ctx.club.id, job_id))


@router.post("/sync/jobs/{job_id}/cancel")
def sync_cancel(job_id: int, ctx: StaffContext = Depends(staff_context), db: DB = Depends(get_db)):
    job = sync.cancel_job(db, ctx.club, ctx.account, job_id, utcnow())
    db.commit()
    return sync.job_out(db, job)


@router.post("/pcs/{pc_id}/ack-hardware")
def ack_pc_hardware(pc_id: int, ctx: StaffContext = Depends(staff_context), db: DB = Depends(get_db)):
    """«Изменена конфигурация»: сотрудник посмотрел и согласен, метка снимается."""
    fleet.ack_hardware(db, ctx.club.id, pc_id)
    return {"ok": True}


@router.post("/pcs/{pc_id}/ack-clone")
def ack_pc_clone(pc_id: int, request: Request, ctx: StaffContext = Depends(staff_context), db: DB = Depends(get_db)):
    """«С ключом этого ПК пробовал подключиться другой компьютер»: сотрудник разобрался, метка снимается."""
    pc = sessions.get_pc(db, ctx.club.id, pc_id)
    pc.clone_alert_at = None
    audit.record(db, ctx.club.id, "pc_clone_ack", utcnow(), actor=ctx.account, pc_id=pc.id, ip=_ip(request))
    db.commit()
    return {"ok": True}


@router.get("/dashboard")
def dashboard(ctx: StaffContext = Depends(staff_context), db: DB = Depends(get_db)):
    """Сводка на сегодня (сутки по времени клуба): загрузка, выручка, касса, очередь заказов и вызовов."""
    return fleet.dashboard(db, ctx.club, utcnow())


def _tariff_out(t: Tariff, club: Club) -> dict:
    return {"id": t.id, "name": t.name, "kind": t.kind, **billing.tariff_prices(t, utcnow(), club.utc_offset), "package_minutes": t.package_minutes,
            "night_start": t.night_start, "night_end": t.night_end, "zone_id": t.zone_id}


def _tariff_fields(data: TariffIn) -> dict:
    return {**data.model_dump(), "day_prices": billing.dump_day_prices(data.day_prices)}


# ---------- залы-шаблоны ----------
@router.get("/zones")
def list_zones(ctx: StaffContext = Depends(staff_context), db: DB = Depends(get_db)):
    """Залы клуба с характеристиками, числом ПК и числом своих тарифов. Общие тарифы клуба (для всех залов) в залах не считаются."""
    pcs, tariffs = zones.counts(db, ctx.club.id)
    return [zones.zone_out(z, pcs=pcs.get(z.id, 0), tariffs=tariffs.get(z.id, 0)) for z in zones.list_zones(db, ctx.club.id)]


@router.post("/zones")
def create_zone(data: ZoneIn, request: Request, ctx: StaffContext = Depends(staff_context), db: DB = Depends(get_db)):
    now = utcnow()
    z = zones.create_zone(db, ctx.club.id, data, now)
    audit.record(db, ctx.club.id, "zone_create", now, actor=ctx.account, details=f"zone={z.id}", ip=_ip(request))
    db.commit()
    return zones.zone_out(z, pcs=0, tariffs=0)


@router.patch("/zones/{zone_id}")
def update_zone(zone_id: int, data: ZonePatch, request: Request, ctx: StaffContext = Depends(staff_context), db: DB = Depends(get_db)):
    z = zones.update_zone(db, ctx.club.id, zone_id, data)
    audit.record(db, ctx.club.id, "zone_update", utcnow(), actor=ctx.account, details=f"zone={z.id}", ip=_ip(request))
    db.commit()
    pcs, tariffs = zones.counts(db, ctx.club.id)
    return zones.zone_out(z, pcs=pcs.get(z.id, 0), tariffs=tariffs.get(z.id, 0))


@router.delete("/zones/{zone_id}")
def delete_zone(zone_id: int, request: Request, ctx: StaffContext = Depends(staff_context), db: DB = Depends(get_db)):
    zones.delete_zone(db, ctx.club.id, zone_id)
    audit.record(db, ctx.club.id, "zone_delete", utcnow(), actor=ctx.account, details=f"zone={zone_id}", ip=_ip(request))
    db.commit()
    return {"ok": True}


@router.post("/zones/{zone_id}/copy-tariffs")
def copy_zone_tariffs(zone_id: int, data: CopyTariffsIn, request: Request, ctx: StaffContext = Depends(staff_context), db: DB = Depends(get_db)):
    """Заполнить зал тарифами другого зала (или общими) одним нажатием; потом цены правятся. Только если у зала своих тарифов ещё нет."""
    copied = zones.copy_tariffs(db, ctx.club.id, zone_id, data.from_zone_id)
    audit.record(db, ctx.club.id, "zone_tariffs_copy", utcnow(), actor=ctx.account, details=f"zone={zone_id} from={data.from_zone_id} n={copied}", ip=_ip(request))
    db.commit()
    return {"copied": copied}


# ---------- тарифы ----------
@router.post("/tariffs")
def create_tariff(data: TariffIn, ctx: StaffContext = Depends(staff_context), db: DB = Depends(get_db)):
    if data.zone_id is not None:
        zones.get_zone(db, ctx.club.id, data.zone_id)           # зал должен быть из этого клуба
    t = Tariff(club_id=ctx.club.id, **_tariff_fields(data))
    db.add(t)
    db.commit()
    return _tariff_out(t, ctx.club)


@router.get("/tariffs")
def list_tariffs(pc_id: int | None = None, ctx: StaffContext = Depends(staff_context), db: DB = Depends(get_db)):
    """Все действующие тарифы клуба (у каждого zone_id: пусто = общий). С pc_id только те, что видит этот ПК: нужно окну «Запустить сессию»."""
    if pc_id is not None:
        pc = sessions.get_pc(db, ctx.club.id, pc_id)
        return [_tariff_out(t, ctx.club) for t in zones.applicable_tariffs(db, ctx.club.id, pc.zone_id)]
    return [_tariff_out(t, ctx.club) for t in db.scalars(select(Tariff).where(Tariff.club_id == ctx.club.id, Tariff.active).order_by(Tariff.id))]


@router.post("/tariffs/{tariff_id}/replace")
def replace_tariff(tariff_id: int, data: TariffIn, request: Request, ctx: StaffContext = Depends(staff_context), db: DB = Depends(get_db)):
    """Изменить тариф (цену, название, условия). Тариф не правится на месте: по нему считаются идущие сессии и отчёты. Поэтому старый отключается
    (идущие сессии доработают по нему, отчёты сохранятся), а вместо него создаётся новый в том же зале. Зал меняется отдельно: переносом ПК."""
    old = sessions.get_tariff(db, ctx.club.id, tariff_id)
    if not old.active:
        raise ServiceError(409, "Тариф уже отключён")
    old.active = False
    new = Tariff(club_id=ctx.club.id, **{**_tariff_fields(data), "zone_id": old.zone_id})
    db.add(new)
    audit.record(db, ctx.club.id, "tariff_replace", utcnow(), actor=ctx.account, details=f"old={old.id}", ip=_ip(request))
    db.commit()
    return _tariff_out(new, ctx.club)


@router.delete("/tariffs/{tariff_id}")
def deactivate_tariff(tariff_id: int, ctx: StaffContext = Depends(staff_context), db: DB = Depends(get_db)):
    t = sessions.get_tariff(db, ctx.club.id, tariff_id)
    t.active = False  # не удаляем, чтобы старые сессии сохранили тариф
    db.commit()
    return {"ok": True}


# ---------- игры и программы в меню гостя ----------
@router.get("/apps")
def list_apps(ctx: StaffContext = Depends(staff_context), db: DB = Depends(get_db)):
    """Весь каталог клуба, включая скрытые записи."""
    meta = covers.meta_for_club(db, ctx.club.id)
    return [catalog.app_out(a, admin=True, cover=meta.get(a.id)) for a in catalog.list_apps(db, ctx.club.id, active_only=False)]


@router.post("/apps")
def create_app(data: AppIn, request: Request, background: BackgroundTasks, ctx: StaffContext = Depends(staff_context), db: DB = Depends(get_db)):
    now = utcnow()
    a = catalog.create_app(db, ctx.club.id, data, now)
    covers.fill_from_programs(db, ctx.club.id, now)          # программа, найденная на ПК, сразу получает свою иконку
    audit.record(db, ctx.club.id, "app_create", now, actor=ctx.account, details=f"app={a.id}", ip=_ip(request))
    db.commit()
    if config.STEAM_AUTOCOVERS and a.launch_kind == "steam" and a.tab == "games":
        background.add_task(covers.autofill_steam, ctx.club.id, force=True)      # постер Steam для новой игры подтянется сам, без кнопки
    return catalog.app_out(a, admin=True, cover=covers.meta_for_club(db, ctx.club.id).get(a.id))


@router.patch("/apps/{app_id}")
def update_app(app_id: int, data: AppPatch, request: Request, background: BackgroundTasks, ctx: StaffContext = Depends(staff_context),
               db: DB = Depends(get_db)):
    a = catalog.update_app(db, ctx.club.id, app_id, data)
    now = utcnow()
    covers.fill_from_programs(db, ctx.club.id, now)
    audit.record(db, ctx.club.id, "app_update", now, actor=ctx.account, details=f"app={a.id}", ip=_ip(request))
    db.commit()
    if config.STEAM_AUTOCOVERS and a.launch_kind == "steam" and a.tab == "games":
        background.add_task(covers.autofill_steam, ctx.club.id, force=True)      # сменили номер игры Steam: постер подтянется сам
    return catalog.app_out(a, admin=True, cover=covers.meta_for_club(db, ctx.club.id).get(a.id))


@router.delete("/apps/{app_id}")
def delete_app(app_id: int, request: Request, ctx: StaffContext = Depends(staff_context), db: DB = Depends(get_db)):
    catalog.delete_app(db, ctx.club.id, app_id)
    audit.record(db, ctx.club.id, "app_delete", utcnow(), actor=ctx.account, details=f"app={app_id}", ip=_ip(request))
    db.commit()
    return {"ok": True}


@router.put("/apps/{app_id}/cover")
async def put_app_cover(app_id: int, request: Request, ctx: StaffContext = Depends(staff_context), db: DB = Depends(get_db)):
    """Загрузить или заменить обложку игры вручную: тело запроса это сам файл (PNG или JPEG, до 2 МБ, лучше вертикальный постер 600 x 900)."""
    declared = request.headers.get("content-length")
    if declared and declared.isdigit() and int(declared) > images.MAX_BYTES:
        raise ServiceError(413, f"Файл больше {images.MAX_BYTES // (1024 * 1024)} МБ: сожмите картинку")
    data = await request.body()
    a = catalog.get_app(db, ctx.club.id, app_id)
    now = utcnow()
    covers.set_cover(db, a, data, "manual", now)
    audit.record(db, ctx.club.id, "app_cover_set", now, actor=ctx.account, details=f"app={a.id}", ip=_ip(request))
    db.commit()
    return catalog.app_out(a, admin=True, cover=covers.meta_for_club(db, ctx.club.id).get(a.id))


@router.get("/apps/{app_id}/cover")
def get_app_cover(app_id: int, ctx: StaffContext = Depends(staff_context), db: DB = Depends(get_db)):
    """Обложка для предпросмотра в админке."""
    a = catalog.get_app(db, ctx.club.id, app_id)
    cover = covers.get_cover(db, a.id)
    if cover is None:
        raise ServiceError(404, "У игры нет обложки")
    return Response(content=cover.data, media_type=cover.content_type)


@router.delete("/apps/{app_id}/cover")
def delete_app_cover(app_id: int, request: Request, ctx: StaffContext = Depends(staff_context), db: DB = Depends(get_db)):
    a = catalog.get_app(db, ctx.club.id, app_id)
    covers.delete_cover(db, a.id)
    audit.record(db, ctx.club.id, "app_cover_delete", utcnow(), actor=ctx.account, details=f"app={a.id}", ip=_ip(request))
    db.commit()
    return {"ok": True}


@router.post("/apps/covers/steam")
def fill_covers_from_steam(request: Request, ctx: StaffContext = Depends(staff_context), db: DB = Depends(get_db)):
    """Подтянуть обложки из Steam всем играм клуба, которые запускаются через Steam и пока без обложки. Ручные обложки не трогает.
    Запрос делает сервер; на много игр может занять до минуты, остаток подтянется при следующем нажатии."""
    now = utcnow()
    result = covers.fill_from_steam(db, ctx.club.id, now)
    audit.record(db, ctx.club.id, "covers_steam", now, actor=ctx.account, details=f"added={result['added']} not_found={result['not_found']}", ip=_ip(request))
    db.commit()
    return result


# ---------- реклама партнёров ----------
@router.get("/ads")
def club_ads(ctx: StaffContext = Depends(staff_context), db: DB = Depends(get_db)):
    """Состояние рекламы в клубе: включена ли, какая доля у клуба, сколько часов экраны показывали рекламу за 30 дней."""
    now = utcnow()
    since = (now + timedelta(hours=ctx.club.utc_offset)).date() - timedelta(days=30)
    return {**ads.club_ads_out(ctx.club), "active_count": len(ads.active_ads(db, now)),
            "screen_hours_30d": ads.club_exposure_hours(db, ctx.club.id, since)}


@router.post("/ads/enabled")
def set_club_ads_enabled(data: ClubAdsToggleIn, request: Request, ctx: StaffContext = Depends(staff_context), db: DB = Depends(get_db)):
    """Клуб сам решает, показывать ли рекламу партнёров на экранах блокировки (за долю в доходе)."""
    ctx.club.ads_enabled = data.enabled
    audit.record(db, ctx.club.id, "ads_enabled" if data.enabled else "ads_disabled", utcnow(), actor=ctx.account, ip=_ip(request))
    db.commit()
    return ads.club_ads_out(ctx.club)


# ---------- бронирование ----------
@router.get("/bookings")
def list_bookings(scope: str = "upcoming", ctx: StaffContext = Depends(staff_context), db: DB = Depends(get_db)):
    """scope=upcoming: действующие и будущие; scope=all: вся история (новые сверху)."""
    return bookings.list_bookings(db, ctx.club.id, utcnow(), upcoming_only=scope != "all")


@router.post("/bookings")
def create_booking(data: BookingIn, request: Request, ctx: StaffContext = Depends(staff_context), db: DB = Depends(get_db)):
    now = utcnow()
    b = bookings.create(db, ctx.club, ctx.account, data, now)
    audit.record(db, ctx.club.id, "booking_create", now, actor=ctx.account, pc_id=b.pc_id, details=f"booking={b.id}", ip=_ip(request))
    db.commit()
    return bookings.booking_out(b, now)


@router.patch("/bookings/{booking_id}")
def update_booking(booking_id: int, data: BookingPatch, request: Request, ctx: StaffContext = Depends(staff_context), db: DB = Depends(get_db)):
    """Изменить бронь: сдвинуть время или длительность, сменить ПК или номер, дописать комментарий (например, «гость просит другую мышь»)."""
    now = utcnow()
    b, changed = bookings.update(db, ctx.club, booking_id, data, now)
    if changed:
        audit.record(db, ctx.club.id, "booking_update", now, actor=ctx.account, pc_id=b.pc_id, details=f"booking={b.id} fields={','.join(changed)}", ip=_ip(request))
    db.commit()
    return bookings.booking_out(b, now)


@router.post("/bookings/{booking_id}/cancel")
def cancel_booking(booking_id: int, request: Request, ctx: StaffContext = Depends(staff_context), db: DB = Depends(get_db)):
    now = utcnow()
    b = bookings.cancel(db, ctx.club.id, booking_id)
    audit.record(db, ctx.club.id, "booking_cancel", now, actor=ctx.account, pc_id=b.pc_id, details=f"booking={b.id}", ip=_ip(request))
    db.commit()
    return bookings.booking_out(b, now)


# ---------- бар: товары, заказы, вызовы ----------
@router.get("/products")
def list_products(ctx: StaffContext = Depends(staff_context), db: DB = Depends(get_db)):
    return [shop.product_out(p, admin=True) for p in shop.list_products(db, ctx.club.id, active_only=False)]


@router.post("/products")
def create_product(data: ProductIn, request: Request, ctx: StaffContext = Depends(staff_context), db: DB = Depends(get_db)):
    now = utcnow()
    p = shop.create_product(db, ctx.club.id, data, now)
    audit.record(db, ctx.club.id, "product_create", now, actor=ctx.account, details=f"product={p.id}", ip=_ip(request))
    db.commit()
    return shop.product_out(p, admin=True)


@router.patch("/products/{product_id}")
def update_product(product_id: int, data: ProductPatch, request: Request, ctx: StaffContext = Depends(staff_context),
                   db: DB = Depends(get_db)):
    p = shop.update_product(db, ctx.club.id, product_id, data)
    audit.record(db, ctx.club.id, "product_update", utcnow(), actor=ctx.account, details=f"product={p.id}", ip=_ip(request))
    db.commit()
    return shop.product_out(p, admin=True)


@router.delete("/products/{product_id}")
def delete_product(product_id: int, request: Request, ctx: StaffContext = Depends(staff_context), db: DB = Depends(get_db)):
    shop.delete_product(db, ctx.club.id, product_id)
    audit.record(db, ctx.club.id, "product_delete", utcnow(), actor=ctx.account, details=f"product={product_id}", ip=_ip(request))
    db.commit()
    return {"ok": True}


@router.get("/orders")
def list_orders(status: str = "open", limit: int = 100, ctx: StaffContext = Depends(staff_context), db: DB = Depends(get_db)):
    """status=open: новые и принятые (то, что ещё нужно принести), старые сверху; status=all: история, новые сверху."""
    return shop.list_orders(db, ctx.club.id, open_only=status != "all", limit=limit)


@router.post("/orders/{order_id}/status")
def set_order_status(order_id: int, data: OrderStatusIn, request: Request, ctx: StaffContext = Depends(staff_context),
                     db: DB = Depends(get_db)):
    """accepted - принят в работу, done - выдан, cancelled - отменён (деньги возвращаются гостю)."""
    o = shop.set_status(db, ctx.club.id, order_id, data.status, ctx.account, utcnow())
    audit.record(db, ctx.club.id, f"order_{data.status}", utcnow(), actor=ctx.account, pc_id=o.pc_id,
                 details=f"order={o.id} amount={o.total}", ip=_ip(request))
    db.commit()
    return shop.order_out(o)


@router.get("/calls")
def list_calls(open_only: bool = True, limit: int = 100, ctx: StaffContext = Depends(staff_context), db: DB = Depends(get_db)):
    return shop.list_calls(db, ctx.club.id, open_only=open_only, limit=limit)


@router.post("/calls/{call_id}/done")
def finish_call(call_id: int, ctx: StaffContext = Depends(staff_context), db: DB = Depends(get_db)):
    return shop.call_out(shop.finish_call(db, ctx.club.id, call_id, ctx.account, utcnow()))


@router.get("/inbox")
def inbox(ctx: StaffContext = Depends(staff_context), db: DB = Depends(get_db)):
    """Сколько заказов и вызовов ждёт сотрудника. Админка спрашивает это раз в несколько секунд для значка в шапке."""
    return shop.inbox(db, ctx.club.id)


# ---------- гости ----------
def _account_out(a: Account) -> dict:
    return {"id": a.id, "phone": a.phone, "balance": a.balance, "status": a.status, "created_at": a.created_at}


@router.get("/accounts")
def find_accounts(phone: str | None = None, q: str | None = None, limit: int = 50,
                  ctx: StaffContext = Depends(staff_context), db: DB = Depends(get_db)):
    """Гости клуба: phone — точный номер (любой формат); q — часть номера, от 3 цифр (удобно искать «по последним цифрам»);
    без параметров — последние зарегистрированные."""
    query = select(Account).where(Account.club_id == ctx.club.id, Account.role == "guest")
    if phone:
        try:
            query = query.where(Account.phone == normalize_phone(phone))
        except ValueError:
            raise ServiceError(422, "Некорректный номер телефона")
    if q:
        digits = "".join(ch for ch in q if ch.isdigit())  # только цифры: безопасно для LIKE
        if len(digits) < 3:
            raise ServiceError(422, "Для поиска введите хотя бы 3 цифры номера")
        query = query.where(Account.phone.contains(digits))
    return [_account_out(a) for a in db.scalars(query.order_by(Account.id.desc()).limit(min(max(limit, 1), 200)))]


@router.get("/accounts/{account_id}/transactions")
def account_transactions(account_id: int, limit: int = 50, ctx: StaffContext = Depends(staff_context),
                         db: DB = Depends(get_db)):
    acc = _guest(db, ctx.club.id, account_id)
    rows = db.scalars(select(WalletTransaction).where(
        WalletTransaction.club_id == ctx.club.id, WalletTransaction.account_id == acc.id
    ).order_by(WalletTransaction.id.desc()).limit(min(max(limit, 1), 500)))
    return [{"id": t.id, "amount": t.amount, "balance_after": t.balance_after, "kind": t.kind,
             "comment": t.comment, "staff_id": t.staff_id, "session_id": t.session_id,
             "created_at": t.created_at} for t in rows]


def _guest_at_pc(db: DB, ctx: StaffContext, pc_id: int):
    pc = sessions.get_pc(db, ctx.club.id, pc_id)
    guest = logins.guest_at_pc(db, ctx.club, pc.id, utcnow())
    db.commit()                      # на ПК мог быть устаревший вход: он закрыт на месте
    return pc, guest


@router.get("/pcs/{pc_id}/guest")
def guest_at_pc(pc_id: int, ctx: StaffContext = Depends(staff_context), db: DB = Depends(get_db)):
    """Кто сейчас за этим ПК (гость с сессией или вошедший в кабинет) и сколько у него на балансе: для окна «Пополнить баланс»."""
    pc, guest = _guest_at_pc(db, ctx, pc_id)
    return {"pc": {"id": pc.id, "name": pc.name}, "guest": _account_out(guest) if guest else None, "max_topup": config.MAX_TOPUP}


@router.post("/pcs/{pc_id}/topup")
def topup_by_pc(pc_id: int, data: MoneyIn, request: Request, ctx: StaffContext = Depends(staff_context), db: DB = Depends(get_db)):
    """Пополнение наличными по номеру ПК: деньги получает гость, который сейчас за этим ПК (сотрудник принял наличные у стойки)."""
    pc, guest = _guest_at_pc(db, ctx, pc_id)
    if guest is None:
        raise ServiceError(409, f"За {pc.name} сейчас нет гостя. Пополнить баланс можно в разделе «Гости» по номеру телефона")
    comment = (data.comment or "").strip() or f"Пополнение за {pc.name}"
    cashier.change_balance(db, ctx.club, ctx.account, guest, data.amount, "topup_cash", comment, utcnow(), _ip(request))
    return {"pc": {"id": pc.id, "name": pc.name}, "guest": _account_out(guest), "amount": data.amount}


@router.post("/accounts/{account_id}/topup")
def topup(account_id: int, data: MoneyIn, request: Request, ctx: StaffContext = Depends(staff_context),
          db: DB = Depends(get_db)):
    """Пополнение баланса гостя наличными (деньги принял сотрудник)."""
    acc = _guest(db, ctx.club.id, account_id)
    cashier.change_balance(db, ctx.club, ctx.account, acc, data.amount, "topup_cash", data.comment,
                           utcnow(), _ip(request))
    return _account_out(acc)


@router.post("/accounts/{account_id}/adjust")
def adjust(account_id: int, data: AdjustIn, request: Request, ctx: StaffContext = Depends(staff_context),
           db: DB = Depends(get_db)):
    """Ручная корректировка баланса (± сумма) — например, исправление ошибки. Комментарий обязателен."""
    acc = _guest(db, ctx.club.id, account_id)
    cashier.change_balance(db, ctx.club, ctx.account, acc, data.amount, "adjustment", data.comment,
                           utcnow(), _ip(request))
    return _account_out(acc)


@router.post("/accounts/{account_id}/block")
def block(account_id: int, request: Request, ctx: StaffContext = Depends(staff_context), db: DB = Depends(get_db)):
    acc = _guest(db, ctx.club.id, account_id)
    cashier.set_blocked(db, ctx.club, ctx.account, acc, True, utcnow(), _ip(request))
    return _account_out(acc)


@router.post("/accounts/{account_id}/unblock")
def unblock(account_id: int, request: Request, ctx: StaffContext = Depends(staff_context),
            db: DB = Depends(get_db)):
    acc = _guest(db, ctx.club.id, account_id)
    cashier.set_blocked(db, ctx.club, ctx.account, acc, False, utcnow(), _ip(request))
    return _account_out(acc)


# ---------- сессии ----------
@router.post("/sessions")
def start_session(data: SessionIn, request: Request, ctx: StaffContext = Depends(staff_context),
                  db: DB = Depends(get_db)):
    """Старт сессии за гостя с его баланса (например, гость попросил администратора)."""
    now = utcnow()
    acc = _guest(db, ctx.club.id, data.account_id)
    s = sessions.start_session(db, ctx.club, acc, data.pc_id, data.tariff_id, now)
    audit.record(db, ctx.club.id, "session_start_by_staff", now, actor=ctx.account, pc_id=s.pc_id,
                 details=f"session={s.id} account={acc.id}", ip=_ip(request))
    db.commit()
    return sessions.session_out(s, now)


@router.post("/sessions/{session_id}/stop")
def stop_session(session_id: int, request: Request, ctx: StaffContext = Depends(staff_context),
                 db: DB = Depends(get_db)):
    now = utcnow()
    s = sessions.stop_session(db, ctx.club.id, session_id, now)
    audit.record(db, ctx.club.id, "session_stop_by_staff", now, actor=ctx.account, pc_id=s.pc_id,
                 details=f"session={s.id}", ip=_ip(request))
    db.commit()
    return sessions.session_out(s, now)


@router.get("/sessions")
def history(limit: int = 50, ctx: StaffContext = Depends(staff_context), db: DB = Depends(get_db)):
    now = utcnow()
    sessions.close_expired(db, ctx.club.id, now)
    rows = db.scalars(select(Session).where(Session.club_id == ctx.club.id)
                      .order_by(Session.id.desc()).limit(min(max(limit, 1), 500))).all()
    pc_names = {pc_id: name for pc_id, name in db.execute(select(PC.id, PC.name).where(PC.club_id == ctx.club.id))}
    phones = {acc_id: phone for acc_id, phone in db.execute(select(Account.id, Account.phone).where(
        Account.id.in_({s.account_id for s in rows})))} if rows else {}
    return [{**sessions.session_out(s, now), "pc_name": pc_names.get(s.pc_id), "guest_phone": phones.get(s.account_id)}
            for s in rows]


def _naive_utc(dt: datetime) -> datetime:
    return dt.astimezone(timezone.utc).replace(tzinfo=None) if dt.tzinfo else dt


@router.get("/report")
def report(since: datetime, until: datetime | None = None,
           ctx: StaffContext = Depends(staff_context), db: DB = Depends(get_db)):
    """Выручка по закрытым сессиям клуба за период (время в UTC). Удобно для сменного отчёта."""
    now = utcnow()
    sessions.close_expired(db, ctx.club.id, now)
    q = select(func.count(Session.id), func.coalesce(func.sum(Session.cost), 0)).where(
        Session.club_id == ctx.club.id, Session.ended_at.is_not(None),
        Session.ended_at >= _naive_utc(since), Session.ended_at <= (_naive_utc(until) if until else now))
    count, total = db.execute(q).one()
    return {"sessions": count, "revenue": total}


@router.get("/report/summary")
def report_summary(since: datetime, until: datetime | None = None,
                   ctx: StaffContext = Depends(staff_context), db: DB = Depends(get_db)):
    """Сводка за смену/период для сдачи кассы: выручка по закрытым сессиям (с разбивкой по тарифам),
    наличные, принятые на пополнение (с разбивкой по сотрудникам), ручные корректировки.
    Время в UTC (допустимы даты с часовым поясом)."""
    now = utcnow()
    start, end = _naive_utc(since), (_naive_utc(until) if until else now)
    if end < start:
        raise ServiceError(422, "Конец периода раньше начала")
    sessions.close_expired(db, ctx.club.id, now)

    in_range_sessions = (Session.club_id == ctx.club.id, Session.ended_at.is_not(None),
                         Session.ended_at >= start, Session.ended_at <= end)
    by_tariff = [{"tariff": name, "sessions": count, "revenue": int(total)} for name, count, total in db.execute(
        select(Tariff.name, func.count(Session.id), func.coalesce(func.sum(Session.cost), 0))
        .join(Tariff, Tariff.id == Session.tariff_id).where(*in_range_sessions)
        .group_by(Tariff.id, Tariff.name).order_by(func.sum(Session.cost).desc()))]

    def money_in(kind: str):
        return (WalletTransaction.club_id == ctx.club.id, WalletTransaction.kind == kind,
                WalletTransaction.created_at >= start, WalletTransaction.created_at <= end)

    cash_by_staff = [{"staff": phone, "amount": int(total), "operations": count} for phone, count, total in db.execute(
        select(Account.phone, func.count(WalletTransaction.id), func.sum(WalletTransaction.amount))
        .join(Account, Account.id == WalletTransaction.staff_id).where(*money_in("topup_cash"))
        .group_by(Account.id, Account.phone).order_by(func.sum(WalletTransaction.amount).desc()))]
    adjustments = db.execute(select(func.coalesce(func.sum(WalletTransaction.amount), 0), func.count(WalletTransaction.id))
                             .where(*money_in("adjustment"))).one()
    shop_orders, shop_revenue = db.execute(select(func.count(Order.id), func.coalesce(func.sum(Order.total), 0)).where(
        Order.club_id == ctx.club.id, Order.status != "cancelled", Order.created_at >= start, Order.created_at <= end)).one()
    return {
        "since": start, "until": end,
        "shop_orders": shop_orders, "shop_revenue": int(shop_revenue),   # бар: заказы за период без отменённых
        "sessions": sum(t["sessions"] for t in by_tariff),
        "revenue": sum(t["revenue"] for t in by_tariff),
        "by_tariff": by_tariff,
        "cash_in": sum(s["amount"] for s in cash_by_staff),
        "cash_by_staff": cash_by_staff,
        "adjustments": int(adjustments[0]), "adjustments_count": adjustments[1],
    }


# ---------- журнал действий ----------
@router.get("/audit")
def audit_log(limit: int = 100, ctx: StaffContext = Depends(staff_context), db: DB = Depends(get_db)):
    rows = db.execute(
        select(AuditLog, Account.phone, Account.role, PC.name)
        .outerjoin(Account, Account.id == AuditLog.actor_account_id)
        .outerjoin(PC, PC.id == AuditLog.pc_id)
        .where(AuditLog.club_id == ctx.club.id)
        .order_by(AuditLog.id.desc()).limit(min(max(limit, 1), 500)))
    return [{"id": r.id, "action": r.action, "actor_account_id": r.actor_account_id,
             "actor_phone": phone, "actor_role": role, "pc_id": r.pc_id, "pc_name": pc_name,
             "details": r.details, "created_at": r.created_at} for r, phone, role, pc_name in rows]
