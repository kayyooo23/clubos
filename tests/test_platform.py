from sqlalchemy import select

from app.db import SessionLocal
from app.models import Account
from app.security import verify_password

from .helpers import (GUEST2, PASSWORD, PHONE_A, PLATFORM, make_club, make_guest, make_pc, make_tariff,
                      staff_login, start_for)


def test_platform_requires_key(client):
    body = {"slug": "x-club", "name": "X", "owner_phone": PHONE_A, "owner_password": PASSWORD}
    assert client.post("/api/platform/clubs", json=body).status_code == 401
    assert client.post("/api/platform/clubs", json=body, headers={"X-Platform-Key": "wrong"}).status_code == 401
    assert client.get("/api/platform/clubs").status_code == 401


def test_create_club_and_owner_can_login(client):
    club = make_club(client, "my-club")
    assert club["slug"] == "my-club" and club["status"] == "active"
    listed = client.get("/api/platform/clubs", headers=PLATFORM).json()
    assert [c["slug"] for c in listed] == ["my-club"]


def test_owner_password_is_hashed_not_stored(client):
    make_club(client, "my-club")
    with SessionLocal() as db:
        acc = db.scalar(select(Account))
        assert acc.password_hash != PASSWORD and acc.password_hash.startswith("$argon2")
        assert verify_password(PASSWORD, acc.password_hash)
        assert acc.role == "staff" and acc.phone_verified_at is not None


def test_duplicate_slug_conflict(client):
    make_club(client, "dup-club")
    r = client.post("/api/platform/clubs", headers=PLATFORM, json={
        "slug": "dup-club", "name": "Другой", "owner_phone": "+79161234567", "owner_password": PASSWORD})
    assert r.status_code == 409


def test_create_club_validation(client):
    base = {"slug": "good-club", "name": "X", "owner_phone": PHONE_A, "owner_password": PASSWORD}
    for bad in ({"owner_phone": "12345"}, {"owner_password": "short"}, {"slug": "Bad Slug!"},
                {"slug": "ab"}, {"utc_offset": 30}):
        assert client.post("/api/platform/clubs", headers=PLATFORM, json={**base, **bad}).status_code == 422, bad


def test_suspended_club_is_locked_out(client):
    club = make_club(client, "club-a")
    pc, tariff = make_pc(client, club), make_tariff(client, club)
    guest = make_guest(client, club, pc, balance=500)
    start_for(client, club, guest, tariff)
    assert client.get("/api/agent/state", headers=pc["headers"]).json()["mode"] == "guest_session"

    r = client.patch(f"/api/platform/clubs/{club['id']}", headers=PLATFORM, json={"status": "suspended"})
    assert r.status_code == 200 and r.json()["status"] == "suspended"

    state = client.get("/api/agent/state", headers=pc["headers"]).json()
    assert state["mode"] == "locked" and state["session"] is None          # даже при идущей сессии
    assert client.get("/api/admin/pcs", headers=club["staff"]).status_code == 403  # старый токен не работает
    login = client.post("/api/admin/auth/login", json={"club": "club-a", "phone": PHONE_A, "password": PASSWORD})
    assert login.status_code == 403
    assert client.get("/api/me", headers=guest["headers"]).status_code == 403                        # гость тоже
    pc_login = client.post("/api/auth/login", json={"phone": PHONE_A, "password": PASSWORD}, headers=pc["headers"])
    assert pc_login.status_code == 403
    assert client.post("/api/auth/register/request-code", json={"phone": GUEST2}, headers=pc["headers"]).status_code == 403

    client.patch(f"/api/platform/clubs/{club['id']}", headers=PLATFORM, json={"status": "active"})
    staff_login(client, "club-a", PHONE_A, PASSWORD)  # снова можно войти
    assert client.get("/api/agent/state", headers=pc["headers"]).json()["mode"] == "guest_session"


def test_platform_resets_staff_password_not_sms(client):
    club = make_club(client)
    pc = make_pc(client, club)
    old_token = club["staff"]["Authorization"].removeprefix("Bearer ")
    # по SMS пароль сотрудника не сбрасывается: SMS не уходит, подтвердить нечем
    assert client.post("/api/auth/password-reset/request-code", json={"phone": PHONE_A}, headers=pc["headers"]).status_code == 200
    assert client.sms.count() == 0
    r = client.post(f"/api/platform/clubs/{club['id']}/reset-staff-password", headers=PLATFORM,
                    json={"phone": PHONE_A, "new_password": "brand-new-password-1"})
    assert r.status_code == 200
    assert client.get("/api/admin/pcs", headers={"Authorization": f"Bearer {old_token}"}).status_code == 401  # старые входы закрыты
    assert client.post("/api/admin/auth/login", json={"club": "club-a", "phone": PHONE_A, "password": PASSWORD}).status_code == 401
    staff_login(client, "club-a", PHONE_A, "brand-new-password-1")
    # без ключа платформы нельзя; пароль слабее нормы нельзя; чужого/несуществующего сотрудника нет
    url = f"/api/platform/clubs/{club['id']}/reset-staff-password"
    assert client.post(url, json={"phone": PHONE_A, "new_password": "brand-new-password-2"}).status_code == 401
    assert client.post(url, headers=PLATFORM, json={"phone": PHONE_A, "new_password": "short"}).status_code == 422
    assert client.post(url, headers=PLATFORM, json={"phone": "+79031234567", "new_password": "brand-new-password-2"}).status_code == 404


def test_unknown_club_status_change(client):
    assert client.patch("/api/platform/clubs/999", headers=PLATFORM, json={"status": "suspended"}).status_code == 404
