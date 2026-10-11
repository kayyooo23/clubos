"""Деньги: пополнение, журнал, списания за сессии, пределы."""
import pytest
from sqlalchemy import select, text
from sqlalchemy.exc import IntegrityError

from app import config
from app.db import SessionLocal
from app.models import Account, WalletTransaction

from .helpers import (GUEST1, GUEST2, balance_of, ledger, make_club, make_guest, make_pc, make_tariff, shift_session,
                      start_for, state, top_up)


@pytest.fixture()
def env(client):
    club = make_club(client)
    pc = make_pc(client, club)
    return client, club, pc


def assert_ledger_matches_balance(client, club, account_id):
    """Главный инвариант: баланс = сумма журнала, а balance_after каждой записи сходится."""
    rows = list(reversed(ledger(client, club, account_id)))
    total = 0
    for r in rows:
        total += r["amount"]
        assert r["balance_after"] == total
    assert balance_of(client, club, account_id) == total


# ---------- пополнение ----------
def test_cash_topup_writes_ledger_and_audit(env):
    client, club, pc = env
    guest = make_guest(client, club, pc)
    r = top_up(client, club, guest["id"], 300, "наличные у стойки")
    assert r["balance"] == 300
    (tx,) = ledger(client, club, guest["id"])
    assert (tx["amount"], tx["balance_after"], tx["comment"]) == (300, 300, "наличные у стойки")
    assert tx["staff_id"] is not None
    assert any(a["action"] == "topup_cash" for a in client.get("/api/admin/audit", headers=club["staff"]).json())
    mine = client.get("/api/me/transactions", headers=guest["headers"]).json()
    assert mine[0]["amount"] == 300 and "comment" not in mine[0]
    assert client.get("/api/me", headers=guest["headers"]).json()["balance"] == 300


@pytest.mark.parametrize("amount", [0, -5, config.MAX_TOPUP + 1, 10.5, "abc", None])
def test_topup_rejects_bad_amounts(env, amount):
    client, club, pc = env
    guest = make_guest(client, club, pc)
    r = client.post(f"/api/admin/accounts/{guest['id']}/topup", json={"amount": amount}, headers=club["staff"])
    assert r.status_code == 422
    assert balance_of(client, club, guest["id"]) == 0


def test_adjustment_needs_comment_and_cannot_go_negative(env):
    client, club, pc = env
    guest = make_guest(client, club, pc, balance=100)
    url = f"/api/admin/accounts/{guest['id']}/adjust"
    assert client.post(url, json={"amount": -10}, headers=club["staff"]).status_code == 422          # без комментария
    assert client.post(url, json={"amount": 0, "comment": "x"}, headers=club["staff"]).status_code == 422
    assert client.post(url, json={"amount": -101, "comment": "ошибка"}, headers=club["staff"]).status_code == 402
    assert balance_of(client, club, guest["id"]) == 100 and len(ledger(client, club, guest["id"])) == 1
    assert client.post(url, json={"amount": -40, "comment": "ошибочное пополнение"}, headers=club["staff"]).status_code == 200
    assert balance_of(client, club, guest["id"]) == 60
    assert_ledger_matches_balance(client, club, guest["id"])


def test_database_itself_refuses_negative_balance(env):
    client, club, pc = env
    guest = make_guest(client, club, pc, balance=10)
    with SessionLocal() as db:
        with pytest.raises(IntegrityError):
            db.execute(text("update accounts set balance = -1 where id = :i"), {"i": guest["id"]})
            db.commit()


def test_find_accounts_by_phone_lists_only_guests(env):
    client, club, pc = env
    make_guest(client, club, pc, GUEST1)
    make_guest(client, club, pc, GUEST2)
    found = client.get("/api/admin/accounts", params={"phone": "8 903 123-45-67"}, headers=club["staff"]).json()
    assert [a["phone"] for a in found] == [GUEST1]
    assert len(client.get("/api/admin/accounts", headers=club["staff"]).json()) == 2   # сотрудник в списке гостей не виден
    assert client.get("/api/admin/accounts", params={"phone": "12"}, headers=club["staff"]).status_code == 422


# ---------- пакет и ночь: списание при старте ----------
def test_package_is_charged_at_start_and_only_once(env):
    client, club, pc = env
    pkg = make_tariff(client, club, name="3 часа", kind="package", price=250, package_minutes=180)
    guest = make_guest(client, club, pc, balance=1000)
    s = start_for(client, club, guest, pkg).json()
    assert balance_of(client, club, guest["id"]) == 750
    shift_session(s["id"], hours=4)                    # пакет истёк
    for _ in range(3):
        assert state(client, pc)["mode"] == "locked"   # много раз читаем состояние
    assert balance_of(client, club, guest["id"]) == 750
    charges = [t for t in ledger(client, club, guest["id"]) if t["kind"] == "session_charge"]
    assert len(charges) == 1 and charges[0]["amount"] == -250 and charges[0]["session_id"] == s["id"]
    assert_ledger_matches_balance(client, club, guest["id"])


def test_not_enough_money_for_package(env):
    client, club, pc = env
    pkg = make_tariff(client, club, name="3 часа", kind="package", price=250, package_minutes=180)
    guest = make_guest(client, club, pc, balance=200)
    r = start_for(client, club, guest, pkg)
    assert r.status_code == 402 and "50" in r.json()["detail"]
    assert balance_of(client, club, guest["id"]) == 200
    assert state(client, pc)["mode"] == "locked"
    assert len(ledger(client, club, guest["id"])) == 1  # в журнале только пополнение


# ---------- почасовой: конец сессии = конец денег ----------
def test_hourly_needs_minimum_balance(env):
    client, club, pc = env
    hourly = make_tariff(client, club, price=120)  # 10 минут = 20 ₽
    poor = make_guest(client, club, pc, balance=19)
    assert start_for(client, club, poor, hourly).status_code == 402
    top_up(client, club, poor["id"], 1)
    assert start_for(client, club, poor, hourly).status_code == 200


def test_hourly_session_length_comes_from_balance(env):
    client, club, pc = env
    hourly = make_tariff(client, club, price=120)
    guest = make_guest(client, club, pc, balance=100)           # 100 ₽ при 120 ₽/ч = 50 минут
    s = start_for(client, club, guest, hourly).json()
    assert abs(s["seconds_left"] - 50 * 60) <= 3
    top_up(client, club, guest["id"], 60)                       # +60 ₽ = +30 минут во время игры
    assert abs(state(client, pc)["session"]["seconds_left"] - 80 * 60) <= 3
    assert balance_of(client, club, guest["id"]) == 160         # с баланса пока ничего не списано


def test_hourly_closes_when_money_runs_out(env):
    client, club, pc = env
    hourly = make_tariff(client, club, price=120)
    guest = make_guest(client, club, pc, balance=100)
    s = start_for(client, club, guest, hourly).json()
    shift_session(s["id"], hours=2)                              # игра шла бы 2 часа, а денег на 50 минут
    assert state(client, pc)["mode"] == "locked"
    assert balance_of(client, club, guest["id"]) == 0            # списано ровно столько, сколько было
    done = client.get("/api/admin/sessions", headers=club["staff"]).json()[0]
    assert done["cost"] == 100
    assert_ledger_matches_balance(client, club, guest["id"])
    # гость остаётся в кабинете и может пополниться и сыграть снова
    assert state(client, pc)["account"]["balance"] == 0
    top_up(client, club, guest["id"], 100)
    assert start_for(client, club, guest, hourly).status_code == 200


def test_topup_does_not_resurrect_an_expired_session(env):
    """Время уже вышло, но никто не читал состояние: пополнение не должно «продлить» закончившуюся сессию бесплатно."""
    client, club, pc = env
    hourly = make_tariff(client, club, price=120)
    guest = make_guest(client, club, pc, balance=100)
    s = start_for(client, club, guest, hourly).json()
    shift_session(s["id"], hours=2)
    top_up(client, club, guest["id"], 500)
    closed = next(x for x in client.get("/api/admin/sessions", headers=club["staff"]).json() if x["id"] == s["id"])
    assert closed["ended_at"] is not None and closed["cost"] == 100
    assert balance_of(client, club, guest["id"]) == 500
    assert_ledger_matches_balance(client, club, guest["id"])


def test_hourly_early_stop_charges_actual_minutes(env):
    client, club, pc = env
    hourly = make_tariff(client, club, price=120)
    guest = make_guest(client, club, pc, balance=1000)
    s = start_for(client, club, guest, hourly).json()
    shift_session(s["id"], minutes=30)
    done = client.post(f"/api/admin/sessions/{s['id']}/stop", headers=club["staff"]).json()
    assert done["cost"] in (60, 61)                              # 30 минут по 120 ₽/ч (+ секунды теста)
    assert balance_of(client, club, guest["id"]) == 1000 - done["cost"]
    assert_ledger_matches_balance(client, club, guest["id"])


def test_stop_never_fails_even_if_balance_is_inconsistent(env):
    """Страховка: если баланс каким-то путём стал меньше набежавшей суммы, закрытие всё равно проходит
    (списывается остаток), а не падает с 402, оставляя гостя с вечно открытой сессией."""
    client, club, pc = env
    hourly = make_tariff(client, club, price=120)
    guest = make_guest(client, club, pc, balance=1000)
    s = start_for(client, club, guest, hourly).json()
    shift_session(s["id"], minutes=60)                           # набежало 120 ₽
    with SessionLocal() as db:                                   # баланс меняем в обход wallet (имитация сбоя)
        db.execute(text("update accounts set balance = 10 where id = :i"), {"i": guest["id"]})
        db.commit()
    done = client.post(f"/api/admin/sessions/{s['id']}/stop", headers=club["staff"])
    assert done.status_code == 200 and done.json()["cost"] == 10
    assert balance_of(client, club, guest["id"]) == 0


def test_hourly_exact_end_of_money_costs_exactly_the_balance(env):
    """Цена 25 ₽/ч, баланс 31 ₽: раньше из-за float списывалось 32 ₽ (см. test_billing)."""
    client, club, pc = env
    hourly = make_tariff(client, club, price=25)
    guest = make_guest(client, club, pc, balance=31)
    s = start_for(client, club, guest, hourly).json()
    shift_session(s["id"], hours=3)
    assert state(client, pc)["mode"] == "locked"
    assert next(x for x in client.get("/api/admin/sessions", headers=club["staff"]).json() if x["id"] == s["id"])["cost"] == 31
    assert balance_of(client, club, guest["id"]) == 0
    assert_ledger_matches_balance(client, club, guest["id"])


def test_adjustment_down_during_hourly_session_never_overdraws(env):
    client, club, pc = env
    hourly = make_tariff(client, club, price=120)
    guest = make_guest(client, club, pc, balance=1000)
    s = start_for(client, club, guest, hourly).json()
    shift_session(s["id"], minutes=60)                           # час игры = 120 ₽
    client.post(f"/api/admin/accounts/{guest['id']}/adjust", json={"amount": -950, "comment": "возврат"},
                headers=club["staff"])                           # остаток 50 ₽ < набежавших 120 ₽
    assert state(client, pc)["mode"] == "locked"                 # денег больше нет — сессия закрыта
    assert balance_of(client, club, guest["id"]) == 0 and balance_of(client, club, guest["id"]) >= 0
    assert_ledger_matches_balance(client, club, guest["id"])


# ---------- сам гость ----------
def test_guest_starts_and_stops_own_session(env):
    client, club, pc = env
    pkg = make_tariff(client, club, name="Час-пакет", kind="package", price=100, package_minutes=60)
    guest = make_guest(client, club, pc, balance=150)
    r = client.post("/api/me/sessions", json={"tariff_id": pkg["id"]}, headers=guest["headers"])
    assert r.status_code == 200 and r.json()["pc_id"] == pc["id"] and r.json()["account_id"] == guest["id"]
    assert state(client, pc)["mode"] == "guest_session" and state(client, pc)["account"]["balance"] == 50
    done = client.post("/api/me/sessions/stop", headers=guest["headers"])
    assert done.status_code == 200 and done.json()["balance"] == 50 and done.json()["cost"] == 100
    assert client.post("/api/me/sessions/stop", headers=guest["headers"]).status_code == 404  # уже нечего останавливать


def test_guest_cannot_start_without_money_or_with_foreign_inputs(env):
    client, club, pc = env
    t = make_tariff(client, club)
    guest = make_guest(client, club, pc)
    assert client.post("/api/me/sessions", json={"tariff_id": t["id"]}, headers=guest["headers"]).status_code == 402
    assert client.post("/api/me/sessions", json={"tariff_id": 999}, headers=guest["headers"]).status_code == 404
    assert client.post("/api/me/sessions", json={"tariff_id": t["id"]}).status_code == 401   # без токена и ключа ПК
    assert client.post("/api/me/sessions", json={"tariff_id": t["id"]}, headers=pc["headers"]).status_code == 401  # без токена


def test_one_session_per_guest_across_pcs(env):
    client, club, pc = env
    pc2 = make_pc(client, club, "PC-2")
    t = make_tariff(client, club)
    guest = make_guest(client, club, pc, balance=1000)
    assert start_for(client, club, guest, t).status_code == 200
    assert start_for(client, club, guest, t, pc=pc2).status_code == 409
    assert state(client, pc2)["mode"] == "locked"


def test_one_guest_cannot_stop_anothers_session(env):
    client, club, pc = env
    pc2, t = make_pc(client, club, "PC-2"), make_tariff(client, club)
    a, b = make_guest(client, club, pc, GUEST1, balance=500), make_guest(client, club, pc2, GUEST2, balance=500)
    start_for(client, club, a, t)
    assert client.post("/api/me/sessions/stop", headers=b["headers"]).status_code == 404
    assert state(client, pc)["mode"] == "guest_session"
    with SessionLocal() as db:  # итоговая проверка по журналу: у b ничего не списано
        assert db.scalar(select(Account.balance).where(Account.id == b["id"])) == 500
        assert len(db.scalars(select(WalletTransaction).where(WalletTransaction.account_id == b["id"])).all()) == 1
