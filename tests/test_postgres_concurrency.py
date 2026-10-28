"""Параллельная работа на PostgreSQL: деньги, сессии и брони при одновременных запросах. На SQLite эти тесты пропускаются: там запись идёт по очереди
и гонок не бывает. Запуск: CLUB_TEST_DB_URL=postgresql+psycopg://postgres@localhost:5433/clubos_test python -m pytest tests/test_postgres_concurrency.py (см. scripts/pg_dev.ps1)."""
import threading
from datetime import datetime, timedelta, timezone

import pytest
from sqlalchemy import func, select

from app.db import SessionLocal
from app.errors import ServiceError
from app.models import Account, Booking, Club, Order, PC, Session, WalletTransaction
from app.schemas import BookingIn, OrderIn
from app.services import bookings, sessions, shop, wallet

from .conftest import ON_POSTGRES
from .helpers import GUEST1, GUEST2, GUEST3, make_club, make_guest, make_pc, make_tariff, shift_session

pytestmark = pytest.mark.skipif(not ON_POSTGRES, reason="гонки проверяются только на PostgreSQL")


def now_utc():
    return datetime.now(timezone.utc).replace(tzinfo=None)


def in_parallel(*jobs):
    """Запускает задания одновременно (по сигналу общего барьера), каждое в своей нити и своём сеансе БД. Возвращает результат или исключение каждого."""
    barrier = threading.Barrier(len(jobs))
    results = [None] * len(jobs)

    def run(i, job):
        try:
            with SessionLocal() as db:
                barrier.wait(timeout=10)
                results[i] = job(db)
        except Exception as e:                       # noqa: BLE001 — нужен именно результат: ошибка тоже ответ
            results[i] = e

    threads = [threading.Thread(target=run, args=(i, job)) for i, job in enumerate(jobs)]
    for t in threads:
        t.start()
    for t in threads:
        t.join(timeout=60)
    return results


@pytest.fixture()
def env(client):
    club = make_club(client)
    pc1, pc2 = make_pc(client, club, "PC-1"), make_pc(client, club, "PC-2")
    hourly = make_tariff(client, club, name="Час", kind="hourly", price=100)
    package = make_tariff(client, club, name="3 часа", kind="package", price=250, package_minutes=180)
    return client, club, pc1, pc2, hourly, package


def test_parallel_charges_never_overdraw_and_the_journal_matches_the_balance(env):
    client, club, pc1, *_ = env
    guest = make_guest(client, club, pc1, phone=GUEST1, balance=1000)

    def charge(db):
        account = db.get(Account, guest["id"])
        try:
            wallet.apply(db, account, -300, "session_charge", now_utc())
            db.commit()
            return "ok"
        except ServiceError as e:
            db.rollback()
            return e.status

    results = in_parallel(*[charge] * 8)
    done = [r for r in results if r == "ok"]
    assert len(done) == 3 and all(r in ("ok", 402) for r in results), results      # 1000 ₽ хватает ровно на три списания по 300
    with SessionLocal() as db:
        balance = db.get(Account, guest["id"]).balance
        journal = db.scalar(select(func.sum(WalletTransaction.amount)).where(WalletTransaction.account_id == guest["id"]))
    assert balance == 100 and journal == balance                                    # баланс не ушёл в минус, журнал сходится с балансом


def test_parallel_top_ups_and_charges_keep_the_sum_exact(env):
    client, club, pc1, *_ = env
    guest = make_guest(client, club, pc1, phone=GUEST1, balance=5000)

    def op(amount):
        def run(db):
            account = db.get(Account, guest["id"])
            wallet.apply(db, account, amount, "topup_cash" if amount > 0 else "session_charge", now_utc())
            db.commit()
            return amount
        return run

    jobs = [op(100)] * 10 + [op(-50)] * 10
    results = in_parallel(*jobs)
    assert all(isinstance(r, int) for r in results), results
    with SessionLocal() as db:
        balance = db.get(Account, guest["id"]).balance
        journal = db.scalar(select(func.sum(WalletTransaction.amount)).where(WalletTransaction.account_id == guest["id"]))
    assert balance == 5000 + 10 * 100 - 10 * 50 and journal == balance


def test_two_guests_starting_on_the_same_pc_at_once_only_one_wins(env):
    client, club, pc1, pc2, hourly, package = env
    a = make_guest(client, club, pc1, phone=GUEST1, balance=1000)
    b = make_guest(client, club, pc2, phone=GUEST2, balance=1000)

    def start(guest_id):
        def run(db):
            club_row = db.get(Club, club["id"])
            account = db.get(Account, guest_id)
            try:
                return sessions.start_session(db, club_row, account, pc1["id"], package["id"], now_utc()).id
            except ServiceError as e:
                db.rollback()
                return e.status
        return run

    results = in_parallel(start(a["id"]), start(b["id"]))
    started = [r for r in results if not isinstance(r, Exception) and r not in (402, 404, 409)]       # номер созданной сессии
    assert len(started) == 1 and results.count(409) == 1, results
    with SessionLocal() as db:
        active = db.scalars(select(Session).where(Session.pc_id == pc1["id"], Session.ended_at.is_(None))).all()
        charged = db.scalar(select(func.count(WalletTransaction.id)).where(WalletTransaction.kind == "session_charge"))
    assert len(active) == 1 and charged == 1                                         # вторая попытка не списала деньги: транзакция откатилась


def test_one_guest_cannot_start_two_sessions_on_two_pcs_at_once(env):
    client, club, pc1, pc2, hourly, package = env
    guest = make_guest(client, club, pc1, phone=GUEST1, balance=1000)

    def start(pc_id):
        def run(db):
            try:
                return sessions.start_session(db, db.get(Club, club["id"]), db.get(Account, guest["id"]), pc_id, package["id"], now_utc()).id
            except ServiceError as e:
                db.rollback()
                return e.status
        return run

    results = in_parallel(start(pc1["id"]), start(pc2["id"]))
    assert len([r for r in results if r not in (402, 409)]) == 1 and results.count(409) == 1, results
    with SessionLocal() as db:
        assert db.scalar(select(func.count(Session.id)).where(Session.account_id == guest["id"], Session.ended_at.is_(None))) == 1
        balance = db.get(Account, guest["id"]).balance
    assert balance == 750                                                            # пакет оплачен один раз


def _hourly_session_that_ran_out(client, club, pc, hourly, phone=GUEST1):
    """Почасовая сессия, у которой уже кончились деньги (время сдвинуто назад): её закрывает первый же запрос, который вызовет close_expired."""
    guest = make_guest(client, club, pc, phone=phone, balance=100)               # 100 ₽ при 100 ₽/ч: час игры
    r = client.post("/api/admin/sessions", json={"pc_id": pc["id"], "tariff_id": hourly["id"], "account_id": guest["id"]}, headers=club["staff"])
    assert r.status_code == 200, r.text
    shift_session(r.json()["id"], hours=2)
    return guest, r.json()["id"]


def test_an_expired_hourly_session_is_charged_once_when_many_requests_close_it_at_once(env):
    client, club, pc1, pc2, hourly, package = env
    guest, session_id = _hourly_session_that_ran_out(client, club, pc1, hourly)

    def close(db):
        sessions.close_expired(db, club["id"], now_utc())
        return "closed"

    in_parallel(*[close] * 6)                                                        # шесть запросов на один и тот же просроченный сеанс
    with SessionLocal() as db:
        charges = db.scalars(select(WalletTransaction).where(WalletTransaction.account_id == guest["id"], WalletTransaction.kind == "session_charge")).all()
        balance = db.get(Account, guest["id"]).balance
        cost = db.get(Session, session_id).cost
    assert len(charges) == 1, [c.amount for c in charges]                            # деньги за сессию списаны один раз
    assert balance == 100 - sum(-c.amount for c in charges) and balance >= 0 and cost == 100


def test_stopping_a_session_twice_at_once_charges_once(env):
    client, club, pc1, pc2, hourly, package = env
    guest = make_guest(client, club, pc1, phone=GUEST1, balance=1000)
    r = client.post("/api/admin/sessions", json={"pc_id": pc1["id"], "tariff_id": hourly["id"], "account_id": guest["id"]}, headers=club["staff"])
    session_id = r.json()["id"]
    shift_session(session_id, minutes=30)                                            # полчаса игры: 50 ₽

    def stop(db):
        try:
            return sessions.stop_session(db, club["id"], session_id, now_utc()).id
        except ServiceError as e:
            db.rollback()
            return e.status

    in_parallel(stop, stop)
    with SessionLocal() as db:
        charges = db.scalars(select(WalletTransaction).where(WalletTransaction.account_id == guest["id"], WalletTransaction.kind == "session_charge")).all()
        balance = db.get(Account, guest["id"]).balance
    assert len(charges) == 1 and balance == 1000 - sum(-c.amount for c in charges), ([c.amount for c in charges], balance)


def test_a_storm_of_mixed_operations_keeps_money_and_sessions_consistent(env):
    """Несколько потоков две-три секунды подряд пополняют баланс, стартуют и останавливают сессии и закрывают просроченные, вразнобой и без пауз.
    После этого: у каждого гостя баланс равен сумме журнала и не отрицателен, на ПК и у гостя не больше одной идущей сессии,
    у закрытых почасовых сессий списано ровно столько, сколько записано в стоимости, и ни один запрос не упал неожиданной ошибкой (взаимная блокировка и т. п.)."""
    import random
    import time

    client, club, pc1, pc2, hourly, package = env
    pc3 = make_pc(client, club, "PC-3")
    guests = [make_guest(client, club, pc, phone=phone, balance=500)
              for pc, phone in [(pc1, GUEST1), (pc2, GUEST2), (pc3, GUEST3), (pc1, "+79051112233"), (pc2, "+79052223344"), (pc3, "+79053334455")]]
    pcs = [pc1["id"], pc2["id"], pc3["id"]]
    product_id = client.post("/api/admin/products", json={"name": "Кола", "category": "Напитки", "price": 90}, headers=club["staff"]).json()["id"]
    import os
    seconds, threads = float(os.environ.get("CLUB_STORM_SECONDS", "3")), int(os.environ.get("CLUB_STORM_THREADS", "8"))   # для ручной проверки на износ можно увеличить
    unexpected = []
    deadline = time.monotonic() + seconds

    def worker(seed):
        rnd = random.Random(seed)

        def run(_db):
            ops = 0
            while time.monotonic() < deadline:
                guest_id, pc_id = rnd.choice(guests)["id"], rnd.choice(pcs)
                action = rnd.choice(["topup", "topup", "start", "stop", "stop", "close", "start", "order", "order", "cancel_order"])
                try:
                    with SessionLocal() as db:
                        now = now_utc()
                        if action == "topup":
                            account = db.get(Account, guest_id)
                            wallet.apply(db, account, rnd.choice([50, 100, 200]), "topup_cash", now)
                            sessions.recalc_limit(db, account, now)
                            db.commit()
                        elif action == "start":
                            sessions.start_session(db, db.get(Club, club["id"]), db.get(Account, guest_id), pc_id, hourly["id"], now)
                        elif action == "stop":
                            s = sessions.active_session_for_account(db, club["id"], guest_id)
                            if s:
                                sessions.stop_session(db, club["id"], s.id, now)
                        elif action == "order":
                            shop.create_order(db, club["id"], db.get(PC, pc_id), db.get(Account, guest_id),
                                              OrderIn(items=[{"product_id": product_id, "qty": rnd.choice([1, 2])}]), now)
                        elif action == "cancel_order":
                            order = db.scalar(select(Order).where(Order.account_id == guest_id, Order.status == "new").order_by(Order.id.desc()))
                            if order:
                                shop.cancel_by_guest(db, club["id"], order.id, guest_id, now)
                        else:
                            sessions.close_expired(db, club["id"], now)
                except ServiceError:
                    pass                                                             # отказ по правилам (нет денег, ПК занят, сессии нет): это нормально
                except Exception as e:                                               # noqa: BLE001 — всё остальное падение, его и ищем
                    unexpected.append(f"{action}: {type(e).__name__}: {str(e)[:160]}")
                ops += 1
            return ops

        return run

    results = in_parallel(*[worker(i) for i in range(threads)])
    assert not unexpected, unexpected[:5]
    assert all(isinstance(r, int) and r > 0 for r in results), results
    with SessionLocal() as db:
        for g in guests:
            balance = db.get(Account, g["id"]).balance
            journal = db.scalar(select(func.coalesce(func.sum(WalletTransaction.amount), 0)).where(WalletTransaction.account_id == g["id"]))
            assert balance >= 0 and balance == journal, (g["phone"], balance, journal)
            assert db.scalar(select(func.count(Session.id)).where(Session.account_id == g["id"], Session.ended_at.is_(None))) <= 1
            # бар: за каждый заказ списано ровно его сумма, за каждый отменённый возвращена ровно она, и ни рублём больше
            bought = db.scalar(select(func.coalesce(func.sum(-WalletTransaction.amount), 0)).where(
                WalletTransaction.account_id == g["id"], WalletTransaction.kind == "shop_purchase"))
            refunded = db.scalar(select(func.coalesce(func.sum(WalletTransaction.amount), 0)).where(
                WalletTransaction.account_id == g["id"], WalletTransaction.kind == "shop_refund"))
            ordered = db.scalar(select(func.coalesce(func.sum(Order.total), 0)).where(Order.account_id == g["id"]))
            cancelled = db.scalar(select(func.coalesce(func.sum(Order.total), 0)).where(Order.account_id == g["id"], Order.status == "cancelled"))
            assert bought == ordered and refunded == cancelled, (g["phone"], bought, ordered, refunded, cancelled)
        for pc_id in pcs:
            assert db.scalar(select(func.count(Session.id)).where(Session.pc_id == pc_id, Session.ended_at.is_(None))) <= 1
        for s in db.scalars(select(Session).where(Session.ended_at.is_not(None))):
            paid = db.scalar(select(func.coalesce(func.sum(-WalletTransaction.amount), 0)).where(
                WalletTransaction.session_id == s.id, WalletTransaction.kind == "session_charge"))
            assert s.cost is not None and s.cost >= 0 and paid == s.cost, (s.id, s.cost, paid)         # почасовая: списано ровно столько, сколько записано


def test_two_bookings_of_the_same_pc_and_time_at_once_only_one_is_created(env):
    client, club, pc1, *_ = env
    start = now_utc() + timedelta(hours=3)

    def book(phone):
        def run(db):
            club_row = db.get(Club, club["id"])
            staff = db.scalar(select(Account).where(Account.club_id == club["id"], Account.role == "staff"))
            try:
                b = bookings.create(db, club_row, staff, BookingIn(pc_id=pc1["id"], phone=phone, starts_at=start, minutes=60), now_utc())
                db.commit()
                return b.id
            except ServiceError as e:
                db.rollback()
                return e.status
        return run

    results = in_parallel(book(GUEST1), book(GUEST2), book(GUEST3))
    assert results.count(409) == 2 and len([r for r in results if r not in (409,)]) == 1, results
    with SessionLocal() as db:
        assert db.scalar(select(func.count(Booking.id)).where(Booking.pc_id == pc1["id"], Booking.status == "active")) == 1


def test_two_sync_jobs_for_the_same_pcs_at_once_only_one_is_created(env):
    from app.models import SyncJob, SyncTarget
    from app.schemas import SyncJobIn
    from app.services import sync
    from .helpers import raw_state

    client, club, pc1, pc2, *_ = env
    third = make_pc(client, club, "PC-3")
    for pc in (pc1, pc2, third):
        raw_state(client, pc)                                                         # ПК «на связи»

    def start(source_id, target_id):
        def run(db):
            club_row = db.get(Club, club["id"])
            staff = db.scalar(select(Account).where(Account.club_id == club["id"], Account.role == "staff"))
            try:
                job, _ = sync.create_job(db, club_row, staff, SyncJobIn(source_pc_id=source_id, paths=["Games/Dota"], target_pc_ids=[target_id]), now_utc())
                db.commit()
                return job.id
            except ServiceError as e:
                db.rollback()
                return e.status
        return run

    # два задания, которым нужен один и тот же ПК (pc2): получатель в одном, эталон в другом
    results = in_parallel(start(pc1["id"], pc2["id"]), start(pc2["id"], third["id"]), start(pc1["id"], pc2["id"]))
    assert len([r for r in results if r == 409]) == 2 and len([r for r in results if r != 409]) == 1, results
    with SessionLocal() as db:
        assert db.scalar(select(func.count(SyncJob.id)).where(SyncJob.status == "running")) == 1
        pcs_in_jobs = list(db.scalars(select(SyncTarget.pc_id)))
        assert len(pcs_in_jobs) == len(set(pcs_in_jobs))                              # ни один ПК не попал в два задания сразу
