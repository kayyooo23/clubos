"""Доплата по часам: когда пакет или ночь кончились, а у гостя ещё есть деньги, игра продолжается по почасовой ставке клуба.
Пополнение продлевает игру, когда деньги кончились, сессия закрывается, а за доплату списывается ровно набежавшее."""
from datetime import datetime, timedelta, timezone

import pytest

from app.db import SessionLocal
from app.models import Session, Tariff
from app.services import billing

from .helpers import (balance_of, ledger, make_club, make_guest, make_pc, make_tariff, shift_session, start_for, state,
                      top_up)

HOUR = 120          # ставка почасового тарифа: 2 ₽ в минуту
PACKAGE = 250       # «3 часа»


@pytest.fixture()
def env(client):
    club = make_club(client)
    pc = make_pc(client, club)
    hourly = make_tariff(client, club, name="Час", kind="hourly", price=HOUR)
    package = make_tariff(client, club, name="3 часа", kind="package", price=PACKAGE, package_minutes=180)
    return client, club, pc, hourly, package


def play(client, club, pc, package, balance):
    guest = make_guest(client, club, pc, balance=balance)
    r = start_for(client, club, guest, package)
    assert r.status_code == 200, r.text
    return guest, r.json()["id"]


def session_of(client, pc):
    return state(client, pc)["session"]


# ---------- как это выглядит для гостя ----------
def test_package_session_reports_the_overtime_rate_and_counts_down_to_the_tariff_end(env):
    client, club, pc, hourly, package = env
    guest, sid = play(client, club, pc, package, 500)
    s = session_of(client, pc)
    assert abs(s["seconds_left"] - 180 * 60) <= 3          # гость видит время до конца тарифа, а не до конца баланса
    assert s["overtime"] is False and s["overtime_price"] == HOUR
    # но на самом деле сессия может идти дольше: остаток баланса 250 ₽ = 125 минут по 2 ₽
    with SessionLocal() as db:
        row = db.get(Session, sid)
        assert row.planned_end - row.base_end == timedelta(minutes=125)
        assert row.base_end - row.started_at == timedelta(minutes=180)


def test_after_the_package_the_session_goes_on_hourly_and_the_clock_shows_the_balance(env):
    client, club, pc, hourly, package = env
    guest, sid = play(client, club, pc, package, 500)
    shift_session(sid, hours=3, minutes=30)                # пакет кончился 30 минут назад
    s = session_of(client, pc)
    assert s is not None, "сессия не должна закрыться: на балансе ещё есть деньги"
    assert s["overtime"] is True
    assert abs(s["seconds_left"] - 95 * 60) <= 3           # 125 минут на балансе минус 30 уже набежавших
    assert PACKAGE + 60 <= s["cost"] <= PACKAGE + 62       # стоимость сессии целиком: пакет + доплата за 30 минут по 2 ₽
    # деньги за доплату списываются при закрытии, а не каждую минуту: баланс пока 250 ₽
    assert balance_of(client, club, guest["id"]) == 250


def test_stopping_during_overtime_charges_only_the_minutes_actually_played(env):
    client, club, pc, hourly, package = env
    guest, sid = play(client, club, pc, package, 500)
    shift_session(sid, hours=3, minutes=30)
    done = client.post("/api/me/sessions/stop", headers=guest["headers"]).json()
    extra = done["cost"] - PACKAGE
    assert 60 <= extra <= 62                               # 30 минут по 2 ₽
    assert balance_of(client, club, guest["id"]) == 250 - extra
    rows = ledger(client, club, guest["id"])               # свежие записи первыми
    assert [r["amount"] for r in rows[:2]] == [-extra, -PACKAGE]
    assert "Доплата по часам" in rows[0]["comment"] and "120" in rows[0]["comment"]
    assert rows[1]["comment"] is None                      # цена пакета при старте списана как раньше


def test_topping_up_during_the_package_or_after_it_extends_the_game(env):
    client, club, pc, hourly, package = env
    guest, sid = play(client, club, pc, package, 250)      # после пакета на балансе 0: доплаты не было бы
    with SessionLocal() as db:
        row = db.get(Session, sid)
        assert row.planned_end == row.base_end

    top_up(client, club, guest["id"], 100)                 # «закинул 100 рублей, чтобы доиграть раунд»
    with SessionLocal() as db:
        row = db.get(Session, sid)
        assert row.planned_end - row.base_end == timedelta(minutes=50)   # 100 ₽ при 2 ₽/мин

    shift_session(sid, hours=3, minutes=20)                # 20 минут после пакета; впереди ещё 30
    s = session_of(client, pc)
    assert s["overtime"] and abs(s["seconds_left"] - 30 * 60) <= 3
    top_up(client, club, guest["id"], 60)                  # ещё 60 ₽ = ещё 30 минут
    assert abs(session_of(client, pc)["seconds_left"] - 60 * 60) <= 3


def test_when_the_money_runs_out_the_session_ends_and_the_charge_never_exceeds_the_balance(env):
    client, club, pc, hourly, package = env
    guest, sid = play(client, club, pc, package, 250 + 101)  # на доплату 101 ₽ = 50 минут 30 секунд
    shift_session(sid, hours=3, minutes=51)                  # прошло больше, чем хватало
    assert state(client, pc)["session"] is None              # ПК снова закрыт
    assert balance_of(client, club, guest["id"]) == 0        # списано всё, что было, и ни рубля сверх
    s = next(x for x in client.get("/api/admin/sessions", headers=club["staff"]).json() if x["id"] == sid)
    assert s["cost"] == PACKAGE + 101 and s["ended_at"] is not None
    assert all(r["balance_after"] >= 0 for r in ledger(client, club, guest["id"]))


def test_zero_balance_after_the_package_means_the_session_ends_exactly_with_the_package(env):
    client, club, pc, hourly, package = env
    guest, sid = play(client, club, pc, package, 250)
    shift_session(sid, hours=3, seconds=5)
    assert state(client, pc)["session"] is None
    s = next(x for x in client.get("/api/admin/sessions", headers=club["staff"]).json() if x["id"] == sid)
    assert s["cost"] == PACKAGE
    assert balance_of(client, club, guest["id"]) == 0


# ---------- когда доплаты нет ----------
def test_without_an_hourly_tariff_in_the_club_the_session_ends_with_the_package(client):
    club = make_club(client)
    pc = make_pc(client, club)
    package = make_tariff(client, club, name="3 часа", kind="package", price=PACKAGE, package_minutes=180)
    guest = make_guest(client, club, pc, balance=1000)
    sid = start_for(client, club, guest, package).json()["id"]
    s = session_of(client, pc)
    assert s["overtime_price"] is None and s["overtime"] is False
    shift_session(sid, hours=3, seconds=5)
    assert state(client, pc)["session"] is None
    assert balance_of(client, club, guest["id"]) == 750


def test_an_hourly_session_is_unchanged(env):
    client, club, pc, hourly, package = env
    guest = make_guest(client, club, pc, balance=240)
    sid = start_for(client, club, guest, hourly).json()["id"]
    s = session_of(client, pc)
    assert s["overtime"] is False and s["overtime_price"] is None
    assert abs(s["seconds_left"] - 120 * 60) <= 3
    with SessionLocal() as db:
        assert db.get(Session, sid).base_end is None


def test_disabling_the_hourly_tariff_later_does_not_change_a_running_session(env):
    client, club, pc, hourly, package = env
    guest, sid = play(client, club, pc, package, 500)
    assert client.delete(f"/api/admin/tariffs/{hourly['id']}", headers=club["staff"]).status_code == 200
    shift_session(sid, hours=3, minutes=30)
    s = session_of(client, pc)
    assert s["overtime"] and s["overtime_price"] == HOUR   # ставка зафиксирована при старте


def test_the_cheapest_active_hourly_tariff_sets_the_rate(client):
    club = make_club(client)
    pc = make_pc(client, club)
    make_tariff(client, club, name="VIP", kind="hourly", price=300)
    make_tariff(client, club, name="Час", kind="hourly", price=150)
    package = make_tariff(client, club, name="3 часа", kind="package", price=PACKAGE, package_minutes=180)
    guest = make_guest(client, club, pc, balance=500)
    start_for(client, club, guest, package)
    assert session_of(client, pc)["overtime_price"] == 150


# ---------- ночной тариф ----------
def test_night_tariff_also_continues_hourly_after_the_night_window(client):
    club = make_club(client, utc_offset=0)
    pc = make_pc(client, club)
    make_tariff(client, club, name="Час", kind="hourly", price=HOUR)
    now_hour = datetime.now(timezone.utc).hour
    night = make_tariff(client, club, name="Ночь", kind="night", price=400,
                        night_start=now_hour, night_end=(now_hour + 5) % 24 or 23)
    guest = make_guest(client, club, pc, balance=600)
    r = start_for(client, club, guest, night)
    assert r.status_code == 200, r.text
    sid = r.json()["id"]
    left = session_of(client, pc)["seconds_left"]
    assert left > 0
    shift_session(sid, seconds=left + 600)                  # ночное окно закончилось 10 минут назад
    s = session_of(client, pc)
    assert s["overtime"] is True and abs(s["seconds_left"] - (100 * 60 - 600)) <= 4   # 200 ₽ при 2 ₽/мин = 100 минут


# ---------- пересчёт при ручной корректировке и арифметика ----------
def test_manual_adjustment_down_shortens_the_overtime_but_never_below_the_tariff_end(env):
    client, club, pc, hourly, package = env
    guest, sid = play(client, club, pc, package, 500)
    r = client.post(f"/api/admin/accounts/{guest['id']}/adjust", headers=club["staff"],
                    json={"amount": -250, "comment": "ошибка кассира"})
    assert r.status_code == 200 and r.json()["balance"] == 0
    with SessionLocal() as db:
        row = db.get(Session, sid)
        assert row.planned_end == row.base_end              # доплаты нет, но оплаченный пакет не отнимается


@pytest.mark.parametrize("rate", [60, 100, 120, 130, 149, 777])
@pytest.mark.parametrize("balance", [0, 1, 7, 31, 99, 100, 250])
def test_overtime_charge_never_exceeds_the_balance_at_the_exact_limit(rate, balance):
    start = datetime(2026, 1, 1, 12, 0)
    base = start + timedelta(hours=3)
    end = billing.overtime_limit_end(base, balance, rate)
    t = Tariff(kind="package", price=250, package_minutes=180)
    s = Session(tariff=t, started_at=start, base_end=base, overtime_price=rate, planned_end=end, ended_at=end)
    assert billing.overtime_cost(s, end) <= balance, (rate, balance)
    one_second_later = Session(tariff=t, started_at=start, base_end=base, overtime_price=rate,
                               planned_end=end + timedelta(seconds=1), ended_at=end + timedelta(seconds=1))
    assert billing.overtime_cost(one_second_later, end) >= 0


def test_overtime_cost_is_zero_before_the_tariff_ends_and_rounds_up_after():
    start = datetime(2026, 1, 1, 12, 0)
    base = start + timedelta(hours=3)
    t = Tariff(kind="package", price=250, package_minutes=180)
    s = Session(tariff=t, started_at=start, base_end=base, overtime_price=120, planned_end=base + timedelta(hours=5))
    assert billing.overtime_cost(s, base - timedelta(minutes=5)) == 0
    assert billing.overtime_cost(s, base) == 0
    assert billing.overtime_cost(s, base + timedelta(seconds=1)) == 1      # любая начатая копейка округляется вверх до рубля
    assert billing.overtime_cost(s, base + timedelta(minutes=1)) == 2
    assert billing.overtime_cost(s, base + timedelta(minutes=30)) == 60
    assert billing.compute_cost(s, base + timedelta(minutes=30)) == 250 + 60
    legacy = Session(tariff=t, started_at=start, planned_end=base)         # старая сессия без доплаты
    assert billing.compute_cost(legacy, base + timedelta(hours=1)) == 250


def test_report_revenue_includes_the_overtime(env):
    client, club, pc, hourly, package = env
    guest, sid = play(client, club, pc, package, 500)
    shift_session(sid, hours=3, minutes=30)
    done = client.post("/api/me/sessions/stop", headers=guest["headers"]).json()
    since = (datetime.now(timezone.utc) - timedelta(days=1)).replace(tzinfo=None).isoformat()
    summary = client.get(f"/api/admin/report/summary?since={since}", headers=club["staff"]).json()
    assert summary["revenue"] == done["cost"] > PACKAGE
