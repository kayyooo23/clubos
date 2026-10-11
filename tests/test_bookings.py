"""Бронирование ПК: создание, пересечения, удержание ПК за гостем, не пришёл, отмена, изоляция клубов."""
from datetime import datetime, timedelta, timezone

import pytest

from app import config
from app.db import SessionLocal
from app.models import Booking

from .helpers import GUEST1, GUEST2, PHONE_B, make_club, make_guest, make_pc, make_tariff, start_for


@pytest.fixture()
def env(client):
    club = make_club(client)
    pc1, pc2 = make_pc(client, club, "PC-1"), make_pc(client, club, "PC-2")
    tariff = make_tariff(client, club, name="Час", kind="hourly", price=100)
    return client, club, pc1, pc2, tariff


def now_utc():
    return datetime.now(timezone.utc).replace(tzinfo=None)


def book(client, club, pc, phone=GUEST1, start_in=timedelta(minutes=10), minutes=60, base=None, **extra):
    """base: одна общая точка отсчёта на несколько броней подряд (иначе «впритык» съезжает на миллисекунды)."""
    body = {"pc_id": pc["id"], "phone": phone, "starts_at": ((base or now_utc()) + start_in).isoformat(), "minutes": minutes, **extra}
    return client.post("/api/admin/bookings", json=body, headers=club["staff"])


def move(booking_id, **delta):
    """«Прошло время»: двигаем бронь назад (например, minutes=30)."""
    with SessionLocal() as db:
        b = db.get(Booking, booking_id)
        b.starts_at -= timedelta(**delta)
        b.ends_at -= timedelta(**delta)
        db.commit()


# ---------- создание ----------
def test_staff_books_a_pc_and_sees_it_in_the_list_and_on_the_map(env):
    client, club, pc1, pc2, _ = env
    r = book(client, club, pc1, phone="8 903 123-45-67", name="  Иван  ", comment="день рождения")
    assert r.status_code == 200, r.text
    b = r.json()
    assert b["phone"] == GUEST1 and b["name"] == "Иван" and b["status"] == "active" and b["holding"] is True   # через 10 минут начало: уже держит
    (listed,) = client.get("/api/admin/bookings", headers=club["staff"]).json()
    assert listed["pc_name"] == "PC-1" and listed["id"] == b["id"]
    pcs = {p["name"]: p for p in client.get("/api/admin/pcs", headers=club["staff"]).json()}
    assert pcs["PC-1"]["booking"]["id"] == b["id"] and pcs["PC-2"]["booking"] is None


def test_far_future_booking_does_not_hold_the_pc_yet(env):
    client, club, pc1, pc2, tariff = env
    r = book(client, club, pc1, start_in=timedelta(hours=5))
    assert r.json()["holding"] is False
    guest = make_guest(client, club, pc1, phone=GUEST2, balance=500)
    assert start_for(client, club, guest, tariff).status_code == 200            # до брони далеко: играть можно


@pytest.mark.parametrize("start_in,minutes,code", [
    (timedelta(hours=-1), 60, 422),                    # время уже прошло
    (timedelta(days=31), 60, 422),                     # слишком далеко
    (timedelta(hours=1), 10, 422),                     # короче 15 минут
    (timedelta(hours=1), 721, 422),                    # длиннее 12 часов
])
def test_bad_booking_times_are_refused(env, start_in, minutes, code):
    client, club, pc1, *_ = env
    assert book(client, club, pc1, start_in=start_in, minutes=minutes).status_code == code


def test_booking_a_few_minutes_in_the_past_is_tolerated(env):
    client, club, pc1, *_ = env
    assert book(client, club, pc1, start_in=timedelta(minutes=-3)).status_code == 200


def test_overlapping_bookings_are_refused_but_neighbours_are_fine(env):
    client, club, pc1, pc2, _ = env
    t0 = now_utc()
    assert book(client, club, pc1, start_in=timedelta(hours=2), minutes=120, base=t0).status_code == 200      # 2ч..4ч
    assert book(client, club, pc1, start_in=timedelta(hours=3), minutes=60, base=t0).status_code == 409        # внутри
    assert book(client, club, pc1, start_in=timedelta(hours=1), minutes=90, base=t0).status_code == 409        # заходит с начала
    assert book(client, club, pc1, start_in=timedelta(hours=3, minutes=30), minutes=120, base=t0).status_code == 409   # заходит с конца
    assert book(client, club, pc1, start_in=timedelta(hours=4), minutes=60, base=t0).status_code == 200        # впритык после
    assert book(client, club, pc1, start_in=timedelta(hours=1), minutes=60, base=t0).status_code == 200        # впритык до
    assert book(client, club, pc2, start_in=timedelta(hours=3), minutes=60, base=t0).status_code == 200        # другой ПК свободен


def test_cancelled_booking_frees_the_time(env):
    client, club, pc1, *_ = env
    bid = book(client, club, pc1, start_in=timedelta(hours=2)).json()["id"]
    assert client.post(f"/api/admin/bookings/{bid}/cancel", headers=club["staff"]).json()["status"] == "cancelled"
    assert client.post(f"/api/admin/bookings/{bid}/cancel", headers=club["staff"]).status_code == 409   # второй раз нельзя
    assert book(client, club, pc1, start_in=timedelta(hours=2)).status_code == 200
    assert [b["id"] for b in client.get("/api/admin/bookings", headers=club["staff"]).json()] != [bid]
    assert any(b["id"] == bid for b in client.get("/api/admin/bookings?scope=all", headers=club["staff"]).json())


def test_pc_on_maintenance_cannot_be_booked(env):
    client, club, pc1, *_ = env
    client.patch(f"/api/admin/pcs/{pc1['id']}", json={"maintenance": True}, headers=club["staff"])
    assert book(client, club, pc1).status_code == 409


# ---------- удержание ПК ----------
def test_a_held_pc_cannot_be_started_by_someone_else_but_the_booked_guest_can(env):
    client, club, pc1, pc2, tariff = env
    bid = book(client, club, pc1, phone=GUEST1, start_in=timedelta(minutes=10)).json()["id"]
    stranger = make_guest(client, club, pc1, phone=GUEST2, balance=500)
    r = start_for(client, club, stranger, tariff)
    assert r.status_code == 409 and "забронирован" in r.json()["detail"]
    # гость, на чей номер бронь, начинает без проблем; бронь становится использованной
    owner = make_guest(client, club, pc2, phone=GUEST1, balance=500)
    r = client.post("/api/admin/sessions", headers=club["staff"], json={"pc_id": pc1["id"], "tariff_id": tariff["id"], "account_id": owner["id"]})
    assert r.status_code == 200, r.text
    with SessionLocal() as db:
        assert db.get(Booking, bid).status == "used"
    assert client.get("/api/admin/bookings", headers=club["staff"]).json() == []                   # использованная в «будущих» не висит


def test_guest_starts_his_own_booked_pc_himself(env):
    client, club, pc1, pc2, tariff = env
    book(client, club, pc1, phone=GUEST1, start_in=timedelta(minutes=5))
    guest = make_guest(client, club, pc1, phone=GUEST1, balance=500)
    r = client.post("/api/me/sessions", json={"tariff_id": tariff["id"]}, headers=guest["headers"])
    assert r.status_code == 200, r.text


def test_hold_starts_half_an_hour_before_and_ends_fifteen_minutes_after_the_start(env):
    client, club, pc1, pc2, tariff = env
    assert (config.BOOKING_LEAD_MINUTES, config.BOOKING_GRACE_MINUTES) == (30, 15)
    stranger = make_guest(client, club, pc1, phone=GUEST2, balance=900)
    bid = book(client, club, pc1, phone=GUEST1, start_in=timedelta(minutes=40), minutes=120).json()["id"]
    assert start_for(client, club, stranger, tariff).status_code == 200                              # за 40 минут ещё можно
    client.post("/api/me/sessions/stop", headers=stranger["headers"])

    move(bid, minutes=15)                                                                            # теперь до начала 25 минут: держит
    assert start_for(client, club, stranger, tariff).status_code == 409

    move(bid, minutes=25 + 16)                                                                       # начало было 16 минут назад: не пришёл
    assert start_for(client, club, stranger, tariff).status_code == 200
    (row,) = client.get("/api/admin/bookings", headers=club["staff"]).json()                         # бронь ещё действует, но «пропущена»
    assert row["missed"] is True and row["holding"] is False


def test_hold_never_outlasts_a_short_booking(env):
    client, club, pc1, pc2, tariff = env
    bid = book(client, club, pc1, phone=GUEST1, start_in=timedelta(minutes=5), minutes=15).json()["id"]
    stranger = make_guest(client, club, pc1, phone=GUEST2, balance=500)
    move(bid, minutes=30)                                                                            # бронь на 15 минут закончилась 10 минут назад
    assert start_for(client, club, stranger, tariff).status_code == 200


def test_booking_only_holds_its_own_pc(env):
    client, club, pc1, pc2, tariff = env
    book(client, club, pc1, phone=GUEST1)
    other = make_guest(client, club, pc2, phone=GUEST2, balance=500)
    assert start_for(client, club, other, tariff).status_code == 200


# ---------- права и изоляция ----------
def test_bookings_are_isolated_between_clubs_and_need_staff_rights(client):
    a = make_club(client, "club-a")
    b = make_club(client, "club-b", phone=PHONE_B)
    pc_a = make_pc(client, a)
    bid = book(client, a, pc_a).json()["id"]
    assert client.get("/api/admin/bookings", headers=b["staff"]).json() == []
    assert client.post(f"/api/admin/bookings/{bid}/cancel", headers=b["staff"]).status_code == 404
    assert book(client, b, pc_a).status_code == 404                                                  # чужой ПК
    guest = make_guest(client, a, pc_a)
    assert client.get("/api/admin/bookings", headers=guest["headers"]).status_code in (401, 403)
    assert client.post("/api/admin/bookings", json={}).status_code in (401, 403, 422)


def test_booking_changes_are_audited(env):
    client, club, pc1, *_ = env
    bid = book(client, club, pc1, start_in=timedelta(hours=3)).json()["id"]
    client.post(f"/api/admin/bookings/{bid}/cancel", headers=club["staff"])
    actions = [a["action"] for a in client.get("/api/admin/audit", headers=club["staff"]).json()]
    assert actions[:2] == ["booking_cancel", "booking_create"]


# ---------- правка брони: комментарий и сдвиг времени ----------
def patch_booking(client, club, booking_id, **body):
    return client.patch(f"/api/admin/bookings/{booking_id}", json=body, headers=club["staff"])


def test_a_comment_can_be_written_and_cleared_without_touching_the_time(env):
    client, club, pc1, pc2, _ = env
    b = book(client, club, pc1, start_in=timedelta(hours=2)).json()
    r = patch_booking(client, club, b["id"], comment="  Гость просит мышь Logitech и клавиатуру с подсветкой  ")
    assert r.status_code == 200, r.text
    assert r.json()["comment"] == "Гость просит мышь Logitech и клавиатуру с подсветкой"
    assert (r.json()["starts_at"], r.json()["ends_at"]) == (b["starts_at"], b["ends_at"])
    (listed,) = client.get("/api/admin/bookings", headers=club["staff"]).json()
    assert listed["comment"].startswith("Гость просит мышь")
    assert patch_booking(client, club, b["id"], comment="").json()["comment"] is None            # пустая строка стирает
    assert any(a["action"] == "booking_update" for a in client.get("/api/admin/audit", headers=club["staff"]).json())


def test_the_start_can_be_moved_later_when_the_guest_calls_that_he_is_late(env):
    client, club, pc1, pc2, _ = env
    b = book(client, club, pc1, start_in=timedelta(minutes=20), minutes=60).json()
    new_start = (now_utc() + timedelta(hours=2)).isoformat()
    r = patch_booking(client, club, b["id"], starts_at=new_start)
    assert r.status_code == 200, r.text
    moved = r.json()
    assert moved["starts_at"].startswith(new_start[:16])
    length = datetime.fromisoformat(moved["ends_at"]) - datetime.fromisoformat(moved["starts_at"])
    assert length == timedelta(minutes=60)                                   # длительность осталась прежней
    assert moved["holding"] is False                                         # до новой брони далеко: ПК снова свободен для других


def test_a_missed_booking_comes_back_to_life_when_it_is_moved(env):
    client, club, pc1, pc2, _ = env
    b = book(client, club, pc1, start_in=timedelta(minutes=10)).json()
    move(b["id"], minutes=40)                                                # гость не пришёл: окно удержания прошло
    assert client.get("/api/admin/bookings", headers=club["staff"]).json()[0]["missed"] is True
    r = patch_booking(client, club, b["id"], starts_at=(now_utc() + timedelta(hours=1)).isoformat())
    assert r.status_code == 200 and r.json()["missed"] is False and r.json()["status"] == "active"


def test_the_length_can_be_changed_and_the_new_end_must_not_overlap_the_next_booking(env):
    client, club, pc1, pc2, _ = env
    base = now_utc()
    first = book(client, club, pc1, start_in=timedelta(hours=1), minutes=60, base=base).json()
    book(client, club, pc1, phone=GUEST2, start_in=timedelta(hours=3), minutes=60, base=base)
    assert patch_booking(client, club, first["id"], minutes=120).status_code == 200            # до соседней брони ровно впритык
    r = patch_booking(client, club, first["id"], minutes=180)
    assert r.status_code == 409 and "уже забронирован" in r.json()["detail"]


def test_moving_onto_another_booking_is_refused_but_onto_its_own_old_slot_is_fine(env):
    client, club, pc1, pc2, _ = env
    base = now_utc()
    a = book(client, club, pc1, start_in=timedelta(hours=1), minutes=60, base=base).json()
    b = book(client, club, pc1, phone=GUEST2, start_in=timedelta(hours=3), minutes=60, base=base).json()
    r = patch_booking(client, club, b["id"], starts_at=(base + timedelta(hours=1, minutes=30)).isoformat())
    assert r.status_code == 409
    # сдвиг на 10 минут внутри собственного окна: сама с собой бронь не пересекается
    assert patch_booking(client, club, a["id"], starts_at=(base + timedelta(hours=1, minutes=10)).isoformat()).status_code == 200


def test_a_booking_can_be_moved_to_another_pc_unless_it_is_taken_or_under_maintenance(env):
    client, club, pc1, pc2, _ = env
    base = now_utc()
    a = book(client, club, pc1, start_in=timedelta(hours=1), minutes=60, base=base).json()
    book(client, club, pc2, phone=GUEST2, start_in=timedelta(hours=1), minutes=60, base=base)
    assert patch_booking(client, club, a["id"], pc_id=pc2["id"]).status_code == 409           # у PC-2 то же время занято
    pc3 = make_pc(client, club, "PC-3")
    r = patch_booking(client, club, a["id"], pc_id=pc3["id"])
    assert r.status_code == 200 and r.json()["pc_id"] == pc3["id"]
    client.patch(f"/api/admin/pcs/{pc3['id']}", json={"maintenance": True}, headers=club["staff"])
    pc4 = make_pc(client, club, "PC-4")
    client.patch(f"/api/admin/pcs/{pc4['id']}", json={"maintenance": True}, headers=club["staff"])
    assert patch_booking(client, club, a["id"], pc_id=pc4["id"]).status_code == 409           # на обслуживании


def test_phone_and_name_can_be_corrected(env):
    client, club, pc1, pc2, _ = env
    b = book(client, club, pc1, start_in=timedelta(hours=2), name="Ваня").json()
    r = patch_booking(client, club, b["id"], phone="8 926 123-45-67", name="Иван Петров")
    assert r.status_code == 200 and r.json()["phone"] == GUEST2 and r.json()["name"] == "Иван Петров"
    assert patch_booking(client, club, b["id"], phone="abc").status_code == 422


@pytest.mark.parametrize("status", ["cancelled", "used"])
def test_closed_bookings_cannot_be_edited(env, status):
    client, club, pc1, pc2, _ = env
    b = book(client, club, pc1, start_in=timedelta(hours=2)).json()
    with SessionLocal() as db:
        db.get(Booking, b["id"]).status = status
        db.commit()
    r = patch_booking(client, club, b["id"], comment="поздно")
    assert r.status_code == 409 and "закрыта" in r.json()["detail"]


def test_a_start_in_the_past_or_too_far_ahead_is_refused_and_nothing_changes(env):
    client, club, pc1, pc2, _ = env
    b = book(client, club, pc1, start_in=timedelta(hours=2)).json()
    assert patch_booking(client, club, b["id"], starts_at=(now_utc() - timedelta(hours=1)).isoformat()).status_code == 422
    assert patch_booking(client, club, b["id"], starts_at=(now_utc() + timedelta(days=60)).isoformat()).status_code == 422
    assert patch_booking(client, club, b["id"], minutes=5).status_code == 422
    assert patch_booking(client, club, b["id"], unknown=1).status_code == 422
    (listed,) = client.get("/api/admin/bookings", headers=club["staff"]).json()
    assert (listed["starts_at"], listed["ends_at"]) == (b["starts_at"], b["ends_at"])


def test_another_club_cannot_edit_the_booking(env):
    client, club, pc1, pc2, _ = env
    b = book(client, club, pc1, start_in=timedelta(hours=2)).json()
    other = make_club(client, slug="club-b", phone=PHONE_B)
    assert patch_booking(client, other, b["id"], comment="чужая").status_code == 404
    assert client.patch(f"/api/admin/bookings/{b['id']}", json={"comment": "x"}).status_code == 401
