import pytest
from sqlalchemy import select

from app import config
from app.db import SessionLocal
from app.models import Account

from .helpers import GUEST1, GUEST2, GUEST_PASSWORD, make_club, make_guest, make_pc

NEW_PASSWORD = "brand-new-pass-9"


@pytest.fixture()
def env(client):
    club = make_club(client)
    pc = make_pc(client, club)
    return client, club, pc


def request_reset(client, pc, phone):
    return client.post("/api/auth/password-reset/request-code", json={"phone": phone}, headers=pc["headers"])


def confirm_reset(client, pc, phone, code, password=NEW_PASSWORD):
    return client.post("/api/auth/password-reset/confirm", headers=pc["headers"],
                       json={"phone": phone, "code": code, "new_password": password})


def pc_login(client, pc, phone, password):
    return client.post("/api/auth/login", json={"phone": phone, "password": password}, headers=pc["headers"])


def test_reset_flow_changes_password_and_kills_old_logins(env):
    client, club, pc = env
    guest = make_guest(client, club, pc, GUEST1)
    assert request_reset(client, pc, GUEST1).status_code == 200
    assert client.sms.count(GUEST1) == 2                      # регистрация + сброс
    assert confirm_reset(client, pc, GUEST1, client.sms.last_code(GUEST1)).status_code == 200
    assert client.get("/api/me", headers=guest["headers"]).status_code == 401   # старый вход закрыт
    assert pc_login(client, pc, GUEST1, GUEST_PASSWORD).status_code == 401
    assert pc_login(client, pc, GUEST1, NEW_PASSWORD).status_code == 200
    assert "password_reset" in [a["action"] for a in client.get("/api/admin/audit", headers=club["staff"]).json()]


def test_reset_clears_lockout(env):
    client, club, pc = env
    make_guest(client, club, pc, GUEST1)
    for _ in range(config.MAX_FAILED_LOGINS):
        pc_login(client, pc, GUEST1, "wrong-password-1")
    assert pc_login(client, pc, GUEST1, GUEST_PASSWORD).status_code == 429
    request_reset(client, pc, GUEST1)
    confirm_reset(client, pc, GUEST1, client.sms.last_code(GUEST1))
    assert pc_login(client, pc, GUEST1, NEW_PASSWORD).status_code == 200


def test_unknown_phone_looks_the_same_and_sends_nothing(env):
    client, club, pc = env
    make_guest(client, club, pc, GUEST1)
    sent = client.sms.count()
    known, unknown = request_reset(client, pc, GUEST1), request_reset(client, pc, GUEST2)
    assert known.status_code == unknown.status_code == 200 and known.json() == unknown.json()
    assert client.sms.count() == sent + 1
    a = confirm_reset(client, pc, GUEST2, "123456")
    b = confirm_reset(client, pc, GUEST1, "000000" if client.sms.last_code(GUEST1) != "000000" else "111111")
    assert a.status_code == b.status_code == 400 and a.json() == b.json()


def test_reset_code_cannot_be_used_to_register_and_vice_versa(env):
    client, club, pc = env
    make_guest(client, club, pc, GUEST1)
    request_reset(client, pc, GUEST1)
    reset_code = client.sms.last_code(GUEST1)
    r = client.post("/api/auth/register/confirm", headers=pc["headers"],
                    json={"phone": GUEST1, "code": reset_code, "password": NEW_PASSWORD, "accept_terms": True})
    assert r.status_code == 400
    assert confirm_reset(client, pc, GUEST1, reset_code).status_code == 200   # а по назначению код работает


def test_reset_code_is_brute_force_protected(env):
    client, club, pc = env
    make_guest(client, club, pc, GUEST1)
    request_reset(client, pc, GUEST1)
    good = client.sms.last_code(GUEST1)
    wrong = "000000" if good != "000000" else "111111"
    for _ in range(config.VERIFY_MAX_ATTEMPTS):
        assert confirm_reset(client, pc, GUEST1, wrong).status_code == 400
    assert confirm_reset(client, pc, GUEST1, good).status_code == 400
    assert pc_login(client, pc, GUEST1, GUEST_PASSWORD).status_code == 200    # пароль остался прежним


def test_weak_new_password_rejected(env):
    client, club, pc = env
    make_guest(client, club, pc, GUEST1)
    request_reset(client, pc, GUEST1)
    assert confirm_reset(client, pc, GUEST1, client.sms.last_code(GUEST1), password="short").status_code == 422
    with SessionLocal() as db:
        assert db.scalar(select(Account).where(Account.phone == GUEST1)).failed_logins == 0
