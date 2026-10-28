"""Цены по дням недели: выходные дороже будней. Цена выбирается по дню недели времени клуба на старте сессии и на всю сессию не меняется."""
from datetime import datetime, timedelta

import pytest

from app.db import SessionLocal
from app.models import Session, Tariff
from app.services import billing
from app.timeutil import utcnow

from .helpers import balance_of, make_club, make_guest, make_pc, make_tariff, start_for, state

OFFSET = 3           # make_club по умолчанию: Москва
MON, FRI, SAT, SUN = 0, 4, 5, 6


def weekday_now() -> int:
    return (utcnow() + timedelta(hours=OFFSET)).weekday()


# ---------- расчёт цены (чистые функции, время задано явно) ----------
def test_price_at_uses_the_weekday_of_the_club_not_of_utc():
    t = Tariff(kind="hourly", price=100, day_prices=billing.dump_day_prices({FRI: 150, SAT: 200}))
    friday_evening_utc = datetime(2026, 10, 16, 20, 0)          # пятница 20:00 UTC = 23:00 в Москве: ещё пятница
    saturday_night_utc = datetime(2026, 10, 16, 21, 30)         # пятница 21:30 UTC = 00:30 в Москве: уже суббота
    assert billing.price_at(t, friday_evening_utc, OFFSET) == 150
    assert billing.price_at(t, saturday_night_utc, OFFSET) == 200
    assert billing.price_at(t, datetime(2026, 10, 14, 12, 0), OFFSET) == 100        # среда: обычная цена


def test_a_tariff_without_day_prices_or_with_broken_json_keeps_the_base_price():
    assert billing.price_at(Tariff(kind="hourly", price=120), datetime(2026, 10, 17, 12, 0), OFFSET) == 120
    assert billing.price_at(Tariff(kind="hourly", price=120, day_prices="не json"), datetime(2026, 10, 17, 12, 0), OFFSET) == 120
    assert billing.day_prices(Tariff(kind="hourly", price=1, day_prices='{"9": 5, "2": -1, "3": 7}')) == {3: 7}      # чужие дни и неположительные цены отбрасываются


def test_the_cost_of_a_running_session_uses_the_price_fixed_at_the_start():
    t = Tariff(kind="hourly", price=100, day_prices=billing.dump_day_prices({SAT: 200}))
    start = datetime(2026, 10, 16, 20, 30)                      # пятница 23:30 в Москве, цена 100
    s = Session(started_at=start, price=100)
    s.tariff = t
    assert billing.compute_cost(s, start + timedelta(hours=2)) == 200          # через полночь (суббота) цена не прыгает: 2 часа по 100
    old = Session(started_at=start)                                            # сессия, начатая до появления цен по дням: цена тарифа
    old.tariff = t
    assert billing.compute_cost(old, start + timedelta(hours=1)) == 100


# ---------- через API ----------
@pytest.fixture()
def env(client):
    club = make_club(client)
    pc = make_pc(client, club)
    return client, club, pc


def test_the_admin_sets_prices_by_day_and_sees_today_s_price(env):
    client, club, pc = env
    today = weekday_now()
    t = make_tariff(client, club, name="Час", kind="hourly", price=100, day_prices={str(today): 180, str((today + 1) % 7): 100})
    assert t["base_price"] == 100 and t["price"] == 180
    assert t["day_prices"] == {str(today): 180}                  # день с обычной ценой не хранится
    listed = {x["id"]: x for x in client.get("/api/admin/tariffs", headers=club["staff"]).json()}
    assert listed[t["id"]]["price"] == 180 and listed[t["id"]]["day_prices"] == {str(today): 180}


def test_the_guest_sees_today_s_price_and_pays_it(env):
    client, club, pc = env
    today = weekday_now()
    t = make_tariff(client, club, name="Пакет", kind="package", price=300, package_minutes=180, day_prices={str(today): 450})
    guest = make_guest(client, club, pc, balance=1000)
    seen = client.get("/api/agent/tariffs", headers=pc["headers"]).json()
    assert [x["price"] for x in seen if x["id"] == t["id"]] == [450]
    r = start_for(client, club, guest, t)
    assert r.status_code == 200, r.text
    assert balance_of(client, club, guest["id"]) == 1000 - 450
    with SessionLocal() as db:
        assert db.get(Session, r.json()["id"]).price == 450
    assert state(client, pc)["session"]["cost"] == 450


def test_another_day_keeps_the_base_price(env):
    client, club, pc = env
    other = (weekday_now() + 3) % 7
    t = make_tariff(client, club, name="Пакет", kind="package", price=300, package_minutes=180, day_prices={str(other): 450})
    guest = make_guest(client, club, pc, balance=1000)
    assert start_for(client, club, guest, t).status_code == 200
    assert balance_of(client, club, guest["id"]) == 700


def test_the_minimum_balance_to_start_depends_on_today_s_price(env):
    client, club, pc = env
    today = weekday_now()
    t = make_tariff(client, club, name="Час", kind="hourly", price=60, day_prices={str(today): 600})     # 10 минут по 600 ₽/час = 100 ₽
    guest = make_guest(client, club, pc, balance=20)             # хватило бы по обычной цене (10 ₽), по сегодняшней нет
    r = start_for(client, club, guest, t)
    assert r.status_code == 402


def test_changing_the_tariff_keeps_day_prices_and_a_zone_copy_carries_them(env):
    client, club, pc = env
    t = make_tariff(client, club, name="Час", kind="hourly", price=100, day_prices={str(SAT): 150, str(SUN): 150})
    body = {"name": "Час", "kind": "hourly", "price": 110, "day_prices": {str(SAT): 170}}
    new = client.post(f"/api/admin/tariffs/{t['id']}/replace", json=body, headers=club["staff"]).json()
    assert new["base_price"] == 110 and new["day_prices"] == {str(SAT): 170}
    zone = client.post("/api/admin/zones", json={"name": "VIP"}, headers=club["staff"]).json()
    r = client.post(f"/api/admin/zones/{zone['id']}/copy-tariffs", json={"from_zone_id": None}, headers=club["staff"])
    assert r.status_code == 200
    with SessionLocal() as db:
        copied = db.query(Tariff).filter(Tariff.zone_id == zone["id"]).one()
        assert billing.day_prices(copied) == {SAT: 170}


@pytest.mark.parametrize("bad", [{"7": 100}, {"-1": 100}, {"3": 0}, {"3": -5}, {"x": 5}])
def test_wrong_day_prices_are_rejected(env, bad):
    client, club, pc = env
    r = client.post("/api/admin/tariffs", json={"name": "Час", "kind": "hourly", "price": 100, "day_prices": bad}, headers=club["staff"])
    assert r.status_code == 422
