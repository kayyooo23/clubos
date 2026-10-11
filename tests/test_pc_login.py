"""Вход на ПК: гость → кабинет, сотрудник → рабочий стол, правила «один вход на ПК», бездействие, блокировка."""
from datetime import timedelta

import pytest
from sqlalchemy import select

from app import config
from app.db import SessionLocal
from app.models import Account, Login
from app.timeutil import utcnow

from .helpers import (GUEST1, GUEST2, GUEST_PASSWORD, PASSWORD, PHONE_A, PHONE_B, make_club, make_guest, make_pc,
                      make_tariff, start_for, state)


@pytest.fixture()
def env(client):
    club = make_club(client)
    pc, tariff = make_pc(client, club), make_tariff(client, club)
    return client, club, pc, tariff


def login(client, pc, phone=GUEST1, password=GUEST_PASSWORD):
    return client.post("/api/auth/login", json={"phone": phone, "password": password}, headers=pc["headers"])


def bearer(pc, token):
    return {**pc["headers"], "Authorization": f"Bearer {token}"}


def age_login(login_id=None, minutes=0):
    """«Гость бездействует minutes минут»: сдвигаем last_seen_at входа назад."""
    with SessionLocal() as db:
        lg = db.scalars(select(Login).where(Login.ended_at.is_(None), Login.pc_id.is_not(None))).first() if login_id is None else db.get(Login, login_id)
        lg.last_seen_at = utcnow() - timedelta(minutes=minutes)
        db.commit()


# ---------- гость ----------
def test_guest_login_shows_cabinet_but_pc_stays_locked(env):
    client, club, pc, _ = env
    guest = make_guest(client, club, pc, GUEST1, balance=300)
    assert client.post("/api/auth/logout", headers=guest["headers"]).status_code == 200
    assert state(client, pc) == {"mode": "locked", "account": None, "session": None}
    r = login(client, pc)
    assert r.status_code == 200 and r.json()["mode"] == "locked" and r.json()["account"]["balance"] == 300
    st = state(client, pc)
    assert st["mode"] == "locked" and st["account"]["balance"] == 300 and st["session"] is None


def test_failed_logins_look_the_same_and_lock_out(env):
    client, club, pc, _ = env
    guest = make_guest(client, club, pc, GUEST1)
    client.post("/api/auth/logout", headers=guest["headers"])
    wrong, unknown = login(client, pc, password="wrong-pass-1"), login(client, pc, phone=GUEST2)
    assert wrong.status_code == unknown.status_code == 401 and wrong.json() == unknown.json()
    for _ in range(config.MAX_FAILED_LOGINS - 1):
        login(client, pc, password="wrong-pass-1")
    assert login(client, pc).status_code == 429
    with SessionLocal() as db:
        acc = db.scalar(select(Account).where(Account.phone == GUEST1))
        acc.locked_until = utcnow() - timedelta(seconds=1)
        db.commit()
    assert login(client, pc).status_code == 200


def test_login_accepts_any_phone_format(env):
    client, club, pc, _ = env
    make_guest(client, club, pc, GUEST1)
    assert login(client, pc, phone="8 903 123 45 67").status_code == 200


def test_logout_kills_token_and_is_blocked_during_session(env):
    client, club, pc, tariff = env
    guest = make_guest(client, club, pc, balance=500)
    s = start_for(client, club, guest, tariff).json()
    assert client.post("/api/auth/logout", headers=guest["headers"]).status_code == 409  # сначала завершите сессию
    assert client.post("/api/me/sessions/stop", headers=guest["headers"]).status_code == 200
    assert client.post("/api/auth/logout", headers=guest["headers"]).status_code == 200
    assert client.get("/api/me", headers=guest["headers"]).status_code == 401
    assert s["id"]


def test_idle_guest_is_logged_out_but_player_is_not(env):
    client, club, pc, tariff = env
    guest = make_guest(client, club, pc, balance=500)
    age_login(minutes=10)  # в клубе по умолчанию выход через 5 минут бездействия
    assert state(client, pc)["account"] is None and state(client, pc)["mode"] == "locked"
    assert client.get("/api/me", headers=guest["headers"]).status_code == 401
    # игрок, у которого идёт сессия, по бездействию не вылетает
    guest = make_guest(client, club, pc, GUEST2, balance=500)
    assert start_for(client, club, guest, tariff).status_code == 200
    age_login(minutes=60)
    assert client.get("/api/me", headers=guest["headers"]).status_code == 200
    assert state(client, pc)["mode"] == "guest_session"


def test_new_guest_replaces_previous_cabinet(env):
    client, club, pc, _ = env
    first = make_guest(client, club, pc, GUEST1)
    second = make_guest(client, club, pc, GUEST2)  # регистрация второго гостя на том же ПК
    assert client.get("/api/me", headers=first["headers"]).status_code == 401   # кабинет первого закрыт
    assert client.get("/api/me", headers=second["headers"]).status_code == 200
    assert state(client, pc)["account"]["id"] == second["id"]
    assert login(client, pc, GUEST1).status_code == 200                          # а первый может войти заново
    assert client.get("/api/me", headers=second["headers"]).status_code == 401


def test_other_guest_cannot_log_in_while_someone_plays(env):
    client, club, pc, tariff = env
    player = make_guest(client, club, pc, GUEST1, balance=500)
    other = make_guest(client, club, make_pc(client, club, "PC-2"), GUEST2)
    assert start_for(client, club, player, tariff).status_code == 200
    assert login(client, pc, GUEST2).status_code == 409
    assert client.get("/api/me", headers=player["headers"]).status_code == 200  # игрок не выбит
    assert other["id"]


def test_player_can_relogin_on_same_pc(env):
    client, club, pc, tariff = env
    guest = make_guest(client, club, pc, balance=500)
    start_for(client, club, guest, tariff)
    r = login(client, pc)
    assert r.status_code == 200
    assert client.get("/api/me", headers=guest["headers"]).status_code == 401   # старый токен мёртв
    assert client.get("/api/me", headers=bearer(pc, r.json()["token"])).status_code == 200
    assert state(client, pc)["mode"] == "guest_session"                        # сессия продолжается


def test_blocked_guest_loses_access_and_session(env):
    client, club, pc, tariff = env
    guest = make_guest(client, club, pc, balance=500)
    start_for(client, club, guest, tariff)
    assert client.post(f"/api/admin/accounts/{guest['id']}/block", headers=club["staff"]).status_code == 200
    assert state(client, pc)["mode"] == "locked"
    assert client.get("/api/me", headers=guest["headers"]).status_code == 401
    assert login(client, pc).status_code == 401
    client.post(f"/api/admin/accounts/{guest['id']}/unblock", headers=club["staff"])
    assert login(client, pc).status_code == 200


# ---------- сотрудник: рабочий стол ----------
def staff_login_at_pc(client, pc, phone=PHONE_A, password=PASSWORD):
    return client.post("/api/auth/login", json={"phone": phone, "password": password}, headers=pc["headers"])


def test_staff_login_opens_desktop_and_is_audited(env):
    client, club, pc, _ = env
    r = staff_login_at_pc(client, pc)
    assert r.status_code == 200 and r.json()["mode"] == "staff_desktop"
    assert state(client, pc) == {"mode": "staff_desktop", "account": {"role": "staff"}, "session": None}
    assert client.get("/api/admin/pcs", headers=club["staff"]).json()[0]["mode"] == "staff_desktop"
    token_headers = bearer(pc, r.json()["token"])
    assert client.post("/api/auth/logout", headers=token_headers).status_code == 200
    assert state(client, pc)["mode"] == "locked"
    actions = [a["action"] for a in client.get("/api/admin/audit", headers=club["staff"]).json()]
    assert "staff_unlock" in actions and "staff_lock" in actions


def test_staff_pc_token_does_not_open_the_admin_api(env):
    client, club, pc, _ = env
    token = staff_login_at_pc(client, pc).json()["token"]
    assert client.get("/api/admin/pcs", headers={"Authorization": f"Bearer {token}"}).status_code == 403
    assert client.get("/api/me", headers=bearer(pc, token)).status_code == 403  # и кабинет гостя тоже не для сотрудника


def test_guest_cannot_log_in_over_staff_desktop(env):
    client, club, pc, _ = env
    make_guest(client, club, make_pc(client, club, "PC-2"), GUEST1)
    staff_login_at_pc(client, pc)
    assert login(client, pc, GUEST1).status_code == 409


def test_staff_cannot_take_over_pc_with_guest_session(env):
    client, club, pc, tariff = env
    guest = make_guest(client, club, pc, balance=500)
    start_for(client, club, guest, tariff)
    assert staff_login_at_pc(client, pc).status_code == 409
    assert state(client, pc)["mode"] == "guest_session"


def test_staff_login_closes_guest_cabinet(env):
    client, club, pc, _ = env
    guest = make_guest(client, club, pc)
    assert staff_login_at_pc(client, pc).status_code == 200
    assert client.get("/api/me", headers=guest["headers"]).status_code == 401


def test_cannot_start_session_on_pc_with_staff_desktop(env):
    client, club, pc, tariff = env
    guest = make_guest(client, club, make_pc(client, club, "PC-2"), balance=500)
    staff_login_at_pc(client, pc)
    assert start_for(client, club, guest, tariff, pc=pc).status_code == 409


def test_force_lock_ends_desktop_but_not_a_session(env):
    client, club, pc, tariff = env
    staff_login_at_pc(client, pc)
    assert client.post(f"/api/admin/pcs/{pc['id']}/lock", headers=club["staff"]).status_code == 200
    assert state(client, pc)["mode"] == "locked"
    guest = make_guest(client, club, pc, balance=500)
    start_for(client, club, guest, tariff)
    assert client.post(f"/api/admin/pcs/{pc['id']}/lock", headers=club["staff"]).status_code == 409
    actions = [a["action"] for a in client.get("/api/admin/audit", headers=club["staff"]).json()]
    assert "pc_force_lock" in actions


def test_staff_desktop_expires(env):
    client, club, pc, _ = env
    staff_login_at_pc(client, pc)
    with SessionLocal() as db:
        lg = db.scalars(select(Login).where(Login.ended_at.is_(None), Login.pc_id.is_not(None))).first()
        lg.expires_at = utcnow() - timedelta(seconds=1)
        db.commit()
    assert state(client, pc)["mode"] == "locked"


def test_staff_of_other_club_cannot_unlock_this_pc(env):
    client, club, pc, _ = env
    make_club(client, "club-b", PHONE_B, "another-password-1")
    assert staff_login_at_pc(client, pc, PHONE_B, "another-password-1").status_code == 401
    assert state(client, pc)["mode"] == "locked"


def test_staff_failed_logins_lock_out(env):
    client, club, pc, _ = env
    for _ in range(config.MAX_FAILED_LOGINS):
        assert staff_login_at_pc(client, pc, password="wrong-password-1").status_code == 401
    assert staff_login_at_pc(client, pc).status_code == 429


def test_state_carries_club_and_pc_names_for_the_lock_screen(env):
    from .helpers import raw_state
    client, club, pc, _ = env
    st = raw_state(client, pc)
    assert st["club_name"] == club["name"] and st["pc_name"] == "PC-1"
    staff_login_at_pc(client, pc)
    assert raw_state(client, pc)["pc_name"] == "PC-1"           # и в режиме сотрудника
    other = make_club(client, "club-b", PHONE_B)
    assert raw_state(client, make_pc(client, other, "Зал-7"))["pc_name"] == "Зал-7"
