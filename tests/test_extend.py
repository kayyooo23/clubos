"""Продление идущей сессии пакетом часов: гость покупает ещё время посреди игры, цена списывается с баланса сразу.
Если оплаченный тариф уже кончился и шла доплата по часам, она закрывается перед продлением, а новый пакет считается от этого момента."""
from datetime import timedelta

import pytest

from app.db import SessionLocal
from app.models import Booking, Session
from app.timeutil import utcnow

from .helpers import (GUEST2, balance_of, ledger, make_club, make_guest, make_pc, make_tariff, shift_session, start_for,
                      state, top_up)

HOUR = 120          # почасовая ставка: 2 ₽ в минуту
PACKAGE = 250       # «3 часа»


@pytest.fixture()
def env(client):
    club = make_club(client)
    pc = make_pc(client, club)
    hourly = make_tariff(client, club, name="Час", kind="hourly", price=HOUR)
    package = make_tariff(client, club, name="3 часа", kind="package", price=PACKAGE, package_minutes=180)
    short = make_tariff(client, club, name="Час пакетом", kind="package", price=100, package_minutes=60)
    return client, club, pc, hourly, package, short


def play(client, club, pc, tariff, balance):
    guest = make_guest(client, club, pc, balance=balance)
    r = start_for(client, club, guest, tariff)
    assert r.status_code == 200, r.text
    return guest, r.json()["id"]


def extend(client, guest, tariff):
    return client.post("/api/me/sessions/extend", json={"tariff_id": tariff["id"]}, headers=guest["headers"])


def row(sid):
    with SessionLocal() as db:
        s = db.get(Session, sid)
        db.expunge(s)
        return s


# ---------- продление до конца тарифа ----------
def test_extending_adds_the_package_time_charges_the_price_and_recounts_the_limit(env):
    client, club, pc, hourly, package, short = env
    guest, sid = play(client, club, pc, package, 1000)               # после старта на балансе 750
    before = row(sid)
    r = extend(client, guest, package)
    assert r.status_code == 200, r.text
    out = r.json()
    assert out["balance"] == 500
    assert abs(out["seconds_left"] - 360 * 60) <= 3                   # три часа, которые были, и ещё три
    after = row(sid)
    assert after.base_end - before.base_end == timedelta(minutes=180)
    assert after.planned_end - after.base_end == timedelta(minutes=250)   # 500 ₽ при 2 ₽ в минуту ещё хватит на доплату
    assert after.extra_paid == PACKAGE
    rows = ledger(client, club, guest["id"])
    assert rows[0]["amount"] == -PACKAGE and "Продление: 3 часа" in rows[0]["comment"]
    assert out["cost"] == PACKAGE * 2                                  # стоимость сессии: старт и продление


def test_stopping_after_an_extension_costs_both_packages_and_nothing_twice(env):
    client, club, pc, hourly, package, short = env
    guest, sid = play(client, club, pc, package, 1000)
    extend(client, guest, short)
    done = client.post("/api/me/sessions/stop", headers=guest["headers"]).json()
    assert done["cost"] == PACKAGE + 100
    assert balance_of(client, club, guest["id"]) == 1000 - PACKAGE - 100
    assert row(sid).cost == PACKAGE + 100


def test_two_extensions_stack(env):
    client, club, pc, hourly, package, short = env
    guest, sid = play(client, club, pc, package, 1000)
    start = row(sid)
    assert extend(client, guest, short).status_code == 200
    assert extend(client, guest, short).status_code == 200
    s = row(sid)
    assert s.base_end - start.base_end == timedelta(minutes=120)
    assert s.extra_paid == 200
    assert balance_of(client, club, guest["id"]) == 1000 - PACKAGE - 200


# ---------- продление после конца тарифа ----------
def test_extending_during_overtime_settles_the_overtime_first_and_the_new_package_starts_now(env):
    client, club, pc, hourly, package, short = env
    guest, sid = play(client, club, pc, package, 1000)               # на балансе 750
    shift_session(sid, hours=3, minutes=30)                           # пакет кончился 30 минут назад: доплата ~60 ₽
    r = extend(client, guest, package)
    assert r.status_code == 200, r.text
    rows = ledger(client, club, guest["id"])
    accrued = -rows[1]["amount"]
    assert 60 <= accrued <= 62 and "перед продлением" in rows[1]["comment"]
    assert rows[0]["amount"] == -PACKAGE
    assert balance_of(client, club, guest["id"]) == 750 - accrued - PACKAGE
    out = r.json()
    assert not out["overtime"] and abs(out["seconds_left"] - 180 * 60) <= 3     # новые три часа считаются от сейчас
    assert row(sid).extra_paid == accrued + PACKAGE
    done = client.post("/api/me/sessions/stop", headers=guest["headers"]).json()
    assert done["cost"] == PACKAGE + accrued + PACKAGE                 # старт + закрытая доплата + продление, без повторов
    assert balance_of(client, club, guest["id"]) == 1000 - done["cost"]


# ---------- отказы ----------
def test_not_enough_money_changes_nothing(env):
    client, club, pc, hourly, package, short = env
    guest, sid = play(client, club, pc, package, 300)                 # после старта 50 ₽
    before = row(sid)
    r = extend(client, guest, package)
    assert r.status_code == 402 and "Не хватает 200" in r.json()["detail"]
    assert row(sid).base_end == before.base_end and row(sid).extra_paid == 0
    assert balance_of(client, club, guest["id"]) == 50


def test_the_overtime_already_run_up_counts_against_the_balance(env):
    client, club, pc, hourly, package, short = env
    guest, sid = play(client, club, pc, package, 560)                 # после старта 310 ₽
    shift_session(sid, hours=3, minutes=30)                           # доплата ~61 ₽ + пакет 250 ₽ = 311 ₽ > 310 ₽
    r = extend(client, guest, package)
    assert r.status_code == 402 and "Не хватает" in r.json()["detail"]
    assert balance_of(client, club, guest["id"]) == 310               # ничего не списалось


def test_an_hourly_session_cannot_be_extended_and_only_packages_can_extend(env):
    client, club, pc, hourly, package, short = env
    guest, sid = play(client, club, pc, hourly, 1000)
    assert extend(client, guest, package).status_code == 409
    client.post("/api/me/sessions/stop", headers=guest["headers"])

    guest2 = make_guest(client, club, pc, phone=GUEST2, balance=1000)       # другой номер: повторный запрос кода на тот же номер ограничен по времени
    assert start_for(client, club, guest2, package).status_code == 200
    assert extend(client, guest2, hourly).status_code == 400          # почасовой тариф продлением не бывает
    off = make_tariff(client, club, name="Выключенный", kind="package", price=50, package_minutes=30)
    assert client.delete(f"/api/admin/tariffs/{off['id']}", headers=club["staff"]).status_code == 200      # «удаление» тарифа его только выключает
    assert extend(client, guest2, off).status_code == 404
    assert client.post("/api/me/sessions/extend", json={"tariff_id": 99999}, headers=guest2["headers"]).status_code == 404


def test_without_a_session_there_is_nothing_to_extend_and_a_token_is_required(env):
    client, club, pc, hourly, package, short = env
    guest = make_guest(client, club, pc, balance=500)
    assert extend(client, guest, package).status_code == 404
    assert client.post("/api/me/sessions/extend", json={"tariff_id": package["id"]}, headers=pc["headers"]).status_code == 401


def test_a_foreign_booking_blocks_the_extension_but_a_missed_one_and_ones_far_away_do_not(env):
    client, club, pc, hourly, package, short = env
    guest, sid = play(client, club, pc, package, 1000)
    now = utcnow()

    def book(starts, phone=GUEST2, status="active"):
        with SessionLocal() as db:
            b = Booking(club_id=club["id"], pc_id=pc["id"], phone=phone, starts_at=starts, ends_at=starts + timedelta(hours=2), status=status)
            db.add(b)
            db.commit()
            return b.id

    far = book(now + timedelta(hours=9))                              # продление закончится раньше: мешать нечему
    assert extend(client, guest, short).status_code == 200
    with SessionLocal() as db:
        db.get(Booking, far).status = "cancelled"
        db.commit()

    book(now + timedelta(hours=5))                                    # чужая бронь попадает в новые два часа
    r = extend(client, guest, package)
    assert r.status_code == 409 and "ПК забронирован" in r.json()["detail"]

    mine = book(now + timedelta(hours=5), phone=guest["phone"])       # своя бронь продлению не мешает
    with SessionLocal() as db:
        for b in db.query(Booking).filter(Booking.phone == GUEST2):
            b.status = "cancelled"
        db.commit()
    assert extend(client, guest, package).status_code == 200
    assert mine


# ---------- клуб без почасового тарифа ----------
def test_without_an_hourly_tariff_the_extension_simply_moves_the_end(client):
    club = make_club(client)
    pc = make_pc(client, club)
    package = make_tariff(client, club, name="3 часа", kind="package", price=PACKAGE, package_minutes=180)
    guest, sid = play(client, club, pc, package, 1000)
    before = row(sid)
    assert before.base_end is None                                    # доплаты нет: сессия кончается вместе с тарифом
    assert extend(client, guest, package).status_code == 200
    after = row(sid)
    assert after.planned_end - before.planned_end == timedelta(minutes=180)
    assert abs(state(client, pc)["session"]["seconds_left"] - 360 * 60) <= 3
    done = client.post("/api/me/sessions/stop", headers=guest["headers"]).json()
    assert done["cost"] == 2 * PACKAGE


def test_topping_up_after_an_extension_still_extends_the_overtime_limit(env):
    client, club, pc, hourly, package, short = env
    guest, sid = play(client, club, pc, package, 500)                 # после старта 250
    assert extend(client, guest, package).status_code == 200          # после продления 0
    s = row(sid)
    assert s.planned_end == s.base_end                                 # денег на доплату нет
    top_up(client, club, guest["id"], 100)
    s = row(sid)
    assert s.planned_end - s.base_end == timedelta(minutes=50)        # пополнение считается от нового конца тарифа
