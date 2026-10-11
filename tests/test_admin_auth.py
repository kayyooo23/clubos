from datetime import timedelta

from sqlalchemy import select

from app import config
from app.db import SessionLocal
from app.models import Account, Club, Login, PC
from app.security import hash_password, hash_secret
from app.timeutil import utcnow

from .helpers import PASSWORD, PHONE_A, make_club, make_pc, staff_login


def login(client, slug="club-a", phone=PHONE_A, password=PASSWORD):
    return client.post("/api/admin/auth/login", json={"club": slug, "phone": phone, "password": password})


def test_login_and_me(client):
    club = make_club(client)
    me = client.get("/api/admin/auth/me", headers=club["staff"]).json()
    assert me["role"] == "staff" and me["club"]["slug"] == "club-a"
    assert PHONE_A not in str(me)  # телефон отдаём только в маске


def test_phone_format_does_not_matter(client):
    make_club(client)
    assert login(client, phone="8 (912) 345-67-89").status_code == 200


def test_all_failures_look_the_same(client):
    make_club(client)
    wrong_pass = login(client, password="wrong-password-1")
    unknown_phone = login(client, phone="+79031234567")
    unknown_club = login(client, slug="nope-club")
    assert wrong_pass.status_code == unknown_phone.status_code == unknown_club.status_code == 401
    assert wrong_pass.json() == unknown_phone.json() == unknown_club.json()


def test_lockout_after_failed_attempts(client):
    make_club(client)
    for _ in range(config.MAX_FAILED_LOGINS):
        assert login(client, password="wrong-password-1").status_code == 401
    assert login(client).status_code == 429  # даже верный пароль не пускает
    with SessionLocal() as db:               # «прошло 15 минут»
        acc = db.scalar(select(Account))
        acc.locked_until = utcnow() - timedelta(seconds=1)
        db.commit()
    assert login(client).status_code == 200


def test_success_resets_failed_counter(client):
    make_club(client)
    for _ in range(config.MAX_FAILED_LOGINS - 1):
        login(client, password="wrong-password-1")
    assert login(client).status_code == 200
    for _ in range(config.MAX_FAILED_LOGINS - 1):  # счётчик начался заново, блокировки нет
        login(client, password="wrong-password-1")
    assert login(client).status_code == 200


def test_logout_kills_token(client):
    club = make_club(client)
    assert client.get("/api/admin/pcs", headers=club["staff"]).status_code == 200
    assert client.post("/api/admin/auth/logout", headers=club["staff"]).status_code == 200
    assert client.get("/api/admin/pcs", headers=club["staff"]).status_code == 401


def test_expired_token_rejected(client):
    club = make_club(client)
    with SessionLocal() as db:
        lg = db.scalar(select(Login))
        lg.expires_at = utcnow() - timedelta(seconds=1)
        db.commit()
    assert client.get("/api/admin/pcs", headers=club["staff"]).status_code == 401


def test_missing_or_garbage_token(client):
    make_club(client)
    assert client.get("/api/admin/pcs").status_code == 401
    assert client.get("/api/admin/pcs", headers={"Authorization": "Bearer garbage"}).status_code == 401
    assert client.get("/api/admin/pcs", headers={"Authorization": "Basic abc"}).status_code == 401


def test_guest_cannot_login_to_admin(client):
    make_club(client)
    with SessionLocal() as db:
        club = db.scalar(select(Club))
        db.add(Account(club_id=club.id, phone="+79031234567", password_hash=hash_password(PASSWORD), role="guest"))
        db.commit()
    r = login(client, phone="+79031234567")
    assert r.status_code == 401 and r.json() == login(client, password="wrong-password-1").json()


def test_blocked_staff_cannot_login_and_old_token_dies(client):
    club = make_club(client)
    with SessionLocal() as db:
        acc = db.scalar(select(Account))
        acc.status = "blocked"
        db.commit()
    assert login(client).status_code == 401
    assert client.get("/api/admin/pcs", headers=club["staff"]).status_code == 403


def test_secrets_are_stored_only_as_hashes(client):
    club = make_club(client)
    pc = make_pc(client, club)
    token = club["staff"]["Authorization"].removeprefix("Bearer ")
    with SessionLocal() as db:
        assert db.scalar(select(Login.token_hash)) == hash_secret(token) != token
        assert db.scalar(select(PC.key_hash)) == hash_secret(pc["key"]) != pc["key"]
