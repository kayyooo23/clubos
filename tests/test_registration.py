from datetime import timedelta

import pytest
from sqlalchemy import select

from app import config
from app.db import SessionLocal
from app.main import app
from app.models import Account, PhoneVerification
from app.services.sms import SmsError, get_sms
from app.timeutil import utcnow

from .helpers import (GUEST1, GUEST2, GUEST3, GUEST_PASSWORD, PHONE_A, PHONE_B, make_club, make_guest, make_pc,
                      make_tariff, staff_login, start_for)


@pytest.fixture()
def env(client):
    club = make_club(client)
    return client, club, make_pc(client, club)


def request_code(client, pc, phone):
    return client.post("/api/auth/register/request-code", json={"phone": phone}, headers=pc["headers"])


def confirm(client, pc, phone, code, password=GUEST_PASSWORD, accept=True):
    return client.post("/api/auth/register/confirm", headers=pc["headers"], json={
        "phone": phone, "code": code, "password": password, "accept_terms": accept})


def expire_cooldown():
    """«Прошла минута»: сдвигаем время создания всех кодов назад."""
    with SessionLocal() as db:
        for row in db.scalars(select(PhoneVerification)):
            row.created_at -= timedelta(seconds=config.SMS_RESEND_SECONDS + 1)
        db.commit()


def test_full_registration_flow(env):
    client, club, pc = env
    r = request_code(client, pc, GUEST1)
    assert r.status_code == 200 and r.json() == {"resend_after": config.SMS_RESEND_SECONDS}
    assert client.sms.count(GUEST1) == 1
    r = confirm(client, pc, GUEST1, client.sms.last_code(GUEST1))
    assert r.status_code == 200
    data = r.json()
    assert data["account"]["role"] == "guest" and data["account"]["balance"] == 0
    assert GUEST1 not in str(data) and "***" in data["account"]["phone_masked"]
    me = client.get("/api/me", headers={**pc["headers"], "Authorization": f"Bearer {data['token']}"})
    assert me.status_code == 200 and me.json()["id"] == data["account"]["id"]
    with SessionLocal() as db:
        acc = db.scalar(select(Account).where(Account.phone == GUEST1))
        assert acc.consent_at is not None and acc.phone_verified_at is not None
        assert acc.password_hash.startswith("$argon2") and GUEST_PASSWORD not in acc.password_hash


def test_phone_is_normalized(env):
    client, _, pc = env
    assert request_code(client, pc, "8 (903) 123-45-67").status_code == 200
    assert client.sms.count("+79031234567") == 1


def test_code_is_stored_hashed(env):
    client, _, pc = env
    request_code(client, pc, GUEST1)
    code = client.sms.last_code(GUEST1)
    with SessionLocal() as db:
        row = db.scalar(select(PhoneVerification))
        assert row.code_hash != code and len(row.code_hash) == 64


def test_wrong_code_attempts_burn_the_code(env):
    client, _, pc = env
    request_code(client, pc, GUEST1)
    good = client.sms.last_code(GUEST1)
    wrong = "000000" if good != "000000" else "111111"
    for _ in range(config.VERIFY_MAX_ATTEMPTS):
        assert confirm(client, pc, GUEST1, wrong).status_code == 400
    assert confirm(client, pc, GUEST1, good).status_code == 400  # код сгорел, даже верный не принимается


def test_code_expires(env):
    client, _, pc = env
    request_code(client, pc, GUEST1)
    with SessionLocal() as db:
        row = db.scalar(select(PhoneVerification))
        row.expires_at = utcnow() - timedelta(seconds=1)
        db.commit()
    assert confirm(client, pc, GUEST1, client.sms.last_code(GUEST1)).status_code == 400


def test_code_is_single_use(env):
    client, _, pc = env
    request_code(client, pc, GUEST1)
    code = client.sms.last_code(GUEST1)
    assert confirm(client, pc, GUEST1, code).status_code == 200
    assert confirm(client, pc, GUEST1, code).status_code == 400


def test_new_code_cancels_old_one(env, monkeypatch):
    client, _, pc = env
    monkeypatch.setattr(config, "SMS_RESEND_SECONDS", 0)
    request_code(client, pc, GUEST1)
    first = client.sms.last_code(GUEST1)
    request_code(client, pc, GUEST1)
    second = client.sms.last_code(GUEST1)
    if first != second:  # (шанс совпадения 1 из миллиона)
        assert confirm(client, pc, GUEST1, first).status_code == 400
    assert confirm(client, pc, GUEST1, second).status_code == 200


def test_resend_cooldown(env):
    client, _, pc = env
    assert request_code(client, pc, GUEST1).status_code == 200
    r = request_code(client, pc, GUEST1)
    assert r.status_code == 429 and "через" in r.json()["detail"]
    assert client.sms.count(GUEST1) == 1
    expire_cooldown()
    assert request_code(client, pc, GUEST1).status_code == 200


def test_per_phone_hourly_limit(env, monkeypatch):
    client, _, pc = env
    monkeypatch.setattr(config, "SMS_RESEND_SECONDS", 0)
    for _ in range(config.SMS_PER_PHONE_PER_HOUR):
        assert request_code(client, pc, GUEST1).status_code == 200
    assert request_code(client, pc, GUEST1).status_code == 429
    assert request_code(client, pc, GUEST2).status_code == 200  # другой номер не страдает


def test_per_pc_hourly_limit(env, monkeypatch):
    client, club, pc = env
    monkeypatch.setattr(config, "SMS_PER_PC_PER_HOUR", 2)
    assert request_code(client, pc, GUEST1).status_code == 200
    assert request_code(client, pc, GUEST2).status_code == 200
    assert request_code(client, pc, GUEST3).status_code == 429
    other_pc = make_pc(client, club, "PC-2")
    assert request_code(client, other_pc, GUEST3).status_code == 200  # лимит на ПК, соседний ПК не затронут


def test_per_club_daily_limit_is_per_club(env, monkeypatch):
    client, club, pc = env
    monkeypatch.setattr(config, "SMS_PER_CLUB_PER_DAY", 2)
    assert request_code(client, pc, GUEST1).status_code == 200
    assert request_code(client, pc, GUEST2).status_code == 200
    r = request_code(client, pc, GUEST3)
    assert r.status_code == 429 and "Лимит" in r.json()["detail"]
    club_b = make_club(client, "club-b", PHONE_B)
    assert request_code(client, make_pc(client, club_b), GUEST3).status_code == 200  # чужой клуб не страдает


def test_registered_phone_is_indistinguishable_from_new(env):
    """По ответам и лимитам нельзя узнать, зарегистрирован ли номер; SMS на занятый номер не уходит."""
    client, club, pc = env
    make_guest(client, club, pc, GUEST1)
    expire_cooldown()
    sent_before = client.sms.count()
    known, unknown = request_code(client, pc, GUEST1), request_code(client, pc, GUEST2)
    assert known.status_code == unknown.status_code == 200 and known.json() == unknown.json()
    assert client.sms.count() == sent_before + 1                       # SMS ушла только на новый номер
    assert request_code(client, pc, GUEST1).status_code == request_code(client, pc, GUEST2).status_code == 429
    # подтверждение для занятого номера отвечает как на неверный код
    a = confirm(client, pc, GUEST1, "123456")
    b = confirm(client, pc, GUEST2, "000000" if client.sms.last_code(GUEST2) != "000000" else "111111")
    assert a.status_code == b.status_code == 400 and a.json() == b.json()


def test_staff_phone_cannot_be_registered_as_guest(env):
    client, _, pc = env
    assert request_code(client, pc, PHONE_A).status_code == 200
    assert client.sms.count(PHONE_A) == 0
    assert confirm(client, pc, PHONE_A, "123456").status_code == 400


def test_validation(env):
    client, _, pc = env
    request_code(client, pc, GUEST1)
    code = client.sms.last_code(GUEST1)
    assert request_code(client, pc, "12345").status_code == 422
    assert confirm(client, pc, GUEST1, code, accept=False).status_code == 422          # без согласия нельзя
    assert confirm(client, pc, GUEST1, code, password="short").status_code == 422
    assert confirm(client, pc, GUEST1, "12345").status_code == 422
    assert confirm(client, pc, GUEST1, "abcdef").status_code == 422
    assert client.post("/api/auth/register/confirm", headers=pc["headers"],
                       json={"phone": GUEST1, "code": code, "password": GUEST_PASSWORD}).status_code == 422
    assert confirm(client, pc, GUEST1, code).status_code == 200                         # после ошибок валидации код цел


def test_sms_provider_failure_is_reported(env):
    client, _, pc = env

    class Broken:
        def send(self, phone, text):
            raise SmsError("provider down")

    app.dependency_overrides[get_sms] = lambda: Broken()
    assert request_code(client, pc, GUEST1).status_code == 502


def test_registration_is_blocked_while_pc_is_busy(env):
    client, club, pc = env
    t = make_tariff(client, club)
    owner = make_guest(client, club, pc, GUEST1, balance=500)
    assert start_for(client, club, owner, t).status_code == 200
    request_code(client, pc, GUEST2)
    assert confirm(client, pc, GUEST2, client.sms.last_code(GUEST2)).status_code == 409  # на ПК играет другой гость


def test_registration_creates_account_only_in_its_club(client):
    a, b = make_club(client, "club-a"), make_club(client, "club-b", PHONE_B)
    make_guest(client, a, make_pc(client, a), GUEST1)
    with SessionLocal() as db:
        accounts = db.scalars(select(Account).where(Account.role == "guest")).all()
        assert [x.club_id for x in accounts] == [a["id"]]
    assert client.post("/api/admin/auth/login",
                       json={"club": "club-b", "phone": GUEST1, "password": GUEST_PASSWORD}).status_code == 401
    staff_login(client, "club-b", PHONE_B, "correct-horse-1")
