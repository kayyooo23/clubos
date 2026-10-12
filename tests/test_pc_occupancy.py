"""Занятость ПК: гость уже за ПК, идёт сессия или сидит сотрудник, и в это же время ПК пытаются забронировать
(администратор у стойки или, позже, сам гость с телефона). Занятый ПК бронировать на это время нельзя, а ПК, за который
администратор посадил гостя, сразу виден как занятый."""
from datetime import timedelta

import pytest

from .helpers import GUEST1, GUEST2, make_club, make_guest, make_pc, make_tariff, raw_state, start_for
from .test_bookings import book, now_utc


@pytest.fixture()
def env(client):
    club = make_club(client)
    pc1, pc2 = make_pc(client, club, "PC-1"), make_pc(client, club, "PC-2")
    hourly = make_tariff(client, club, name="Час", kind="hourly", price=100)
    package = make_tariff(client, club, name="3 часа", kind="package", price=250, package_minutes=180)
    return client, club, pc1, pc2, hourly, package


def pcs_by_name(client, club):
    return {p["name"]: p for p in client.get("/api/admin/pcs", headers=club["staff"]).json()}


def staff_sits_at(client, club, pc):
    r = client.post("/api/auth/login", json={"phone": club["phone"], "password": club["password"]}, headers=pc["headers"])
    assert r.status_code == 200, r.text


# ---------- гость в кабинете: ПК занят ----------
def test_a_guest_in_the_cabinet_is_visible_on_the_map(env):
    client, club, pc1, pc2, *_ = env
    make_guest(client, club, pc1, phone=GUEST1)
    pcs = pcs_by_name(client, club)
    assert pcs["PC-1"]["cabinet_phone"] == GUEST1 and pcs["PC-1"]["mode"] == "locked"      # сессии ещё нет, а ПК уже занят
    assert pcs["PC-2"]["cabinet_phone"] is None


def test_a_pc_with_a_guest_in_the_cabinet_cannot_be_booked_for_now(env):
    client, club, pc1, pc2, *_ = env
    make_guest(client, club, pc1, phone=GUEST1)
    r = book(client, club, pc1, phone=GUEST2, start_in=timedelta(minutes=5))
    assert r.status_code == 409 and "занят" in r.json()["detail"]
    assert book(client, club, pc2, phone=GUEST2, start_in=timedelta(minutes=5)).status_code == 200    # соседний ПК свободен


def test_a_guest_in_the_cabinet_does_not_block_a_booking_for_later(env):
    client, club, pc1, *_ = env
    make_guest(client, club, pc1, phone=GUEST1)
    assert book(client, club, pc1, phone=GUEST2, start_in=timedelta(hours=5)).status_code == 200


def test_a_booking_cannot_be_moved_onto_a_busy_pc_or_into_busy_time(env):
    client, club, pc1, pc2, *_ = env
    make_guest(client, club, pc1, phone=GUEST1)
    later = book(client, club, pc1, phone=GUEST2, start_in=timedelta(hours=5)).json()["id"]
    soon = (now_utc() + timedelta(minutes=5)).isoformat()
    assert client.patch(f"/api/admin/bookings/{later}", json={"starts_at": soon}, headers=club["staff"]).status_code == 409
    other = book(client, club, pc2, phone=GUEST2, start_in=timedelta(minutes=5)).json()["id"]
    assert client.patch(f"/api/admin/bookings/{other}", json={"pc_id": pc1["id"]}, headers=club["staff"]).status_code == 409


# ---------- идёт сессия ----------
def test_a_pc_with_a_package_session_is_busy_until_the_paid_time_ends(env):
    client, club, pc1, pc2, hourly, package = env
    guest = make_guest(client, club, pc1, phone=GUEST1, balance=1000)
    assert start_for(client, club, guest, package).status_code == 200                  # оплачено на 3 часа
    t0 = now_utc()
    assert book(client, club, pc1, phone=GUEST2, start_in=timedelta(minutes=10), base=t0).status_code == 409
    assert book(client, club, pc1, phone=GUEST2, start_in=timedelta(hours=2), base=t0).status_code == 409   # держит ПК с 1:30, ещё играют
    assert book(client, club, pc1, phone=GUEST2, start_in=timedelta(hours=4), base=t0).status_code == 200   # держит с 3:30, оплаченное время вышло


def test_a_pc_with_an_hourly_session_is_busy_now_but_not_for_a_far_booking(env):
    client, club, pc1, pc2, hourly, package = env
    guest = make_guest(client, club, pc1, phone=GUEST1, balance=1000)
    assert start_for(client, club, guest, hourly).status_code == 200
    assert book(client, club, pc1, phone=GUEST2, start_in=timedelta(minutes=10)).status_code == 409
    assert book(client, club, pc1, phone=GUEST2, start_in=timedelta(hours=3)).status_code == 200      # почасовой играет «пока хватает», конца нет


# ---------- сотрудник за ПК ----------
def test_a_pc_with_a_staff_desktop_cannot_be_booked_for_now(env):
    client, club, pc1, *_ = env
    staff_sits_at(client, club, pc1)
    assert book(client, club, pc1, phone=GUEST2, start_in=timedelta(minutes=5)).status_code == 409
    assert book(client, club, pc1, phone=GUEST2, start_in=timedelta(hours=5)).status_code == 200


# ---------- администратор сажает гостя у стойки ----------
def test_a_booking_for_now_marks_the_pc_busy_and_a_second_booking_is_refused(env):
    """Гость пришёл и сказал «хочу в VIP»: администратор занимает ПК на его номер. Другой гость (например, из приложения) не может занять его же."""
    client, club, pc1, pc2, hourly, _ = env
    mine = book(client, club, pc1, phone=GUEST1, start_in=timedelta(minutes=0), minutes=60)
    assert mine.status_code == 200 and mine.json()["holding"] is True
    assert pcs_by_name(client, club)["PC-1"]["booking"]["id"] == mine.json()["id"]
    r = book(client, club, pc1, phone=GUEST2, start_in=timedelta(minutes=0), minutes=60)
    assert r.status_code == 409 and "забронирован" in r.json()["detail"]
    stranger = make_guest(client, club, pc1, phone=GUEST2, balance=500)
    r = start_for(client, club, stranger, hourly)
    assert r.status_code == 409 and "забронирован" in r.json()["detail"]                # и сесть играть чужому не даст


def test_the_lock_screen_of_a_held_pc_says_it_is_booked(env):
    client, club, pc1, pc2, *_ = env
    book(client, club, pc1, phone=GUEST1, start_in=timedelta(minutes=0))
    assert "забронирован" in raw_state(client, pc1)["message"]
    assert "message" not in raw_state(client, pc2)


# ---------- сессию за гостя нельзя запустить поверх чужого гостя ----------
def test_staff_cannot_start_a_session_for_one_guest_while_another_sits_at_the_pc(env):
    client, club, pc1, pc2, hourly, _ = env
    sitting = make_guest(client, club, pc1, phone=GUEST2)                              # уже сидит в кабинете PC-1
    arriving = make_guest(client, club, pc2, phone=GUEST1, balance=500)
    r = start_for(client, club, arriving, hourly, pc=pc1)
    assert r.status_code == 409 and "другой гость" in r.json()["detail"]
    assert client.post(f"/api/admin/pcs/{pc1['id']}/lock", headers=club["staff"]).status_code == 200   # администратор отправил гостя за другой ПК
    assert start_for(client, club, arriving, hourly, pc=pc1).status_code == 200


def test_the_booked_guest_has_priority_over_a_stranger_in_the_cabinet(env):
    client, club, pc1, pc2, hourly, _ = env
    owner = make_guest(client, club, pc2, phone=GUEST1, balance=500)
    assert book(client, club, pc1, phone=GUEST1, start_in=timedelta(minutes=0)).status_code == 200
    stranger = make_guest(client, club, pc1, phone=GUEST2)                             # потом за этот ПК сел случайный гость
    assert start_for(client, club, owner, hourly, pc=pc1).status_code == 200
    assert client.get("/api/me", headers=stranger["headers"]).status_code == 401       # вход чужого гостя в кабинете закрыт


def test_staff_can_start_a_session_for_the_guest_who_sits_at_the_pc(env):
    client, club, pc1, pc2, hourly, _ = env
    guest = make_guest(client, club, pc1, phone=GUEST1, balance=500)
    assert start_for(client, club, guest, hourly).status_code == 200
