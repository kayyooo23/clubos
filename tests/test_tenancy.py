"""Главная гарантия мультиклубности: клуб А не видит и не меняет ничего в клубе Б."""
import pytest
from sqlalchemy import select
from sqlalchemy.exc import IntegrityError

from app.db import SessionLocal
from app.errors import ServiceError
from app.models import Club, Session
from app.services import accounts
from app.timeutil import utcnow

from .helpers import (GUEST1, PASSWORD, PHONE_A, PHONE_B, make_club, make_guest, make_pc, make_tariff,
                      staff_login, start_for, state)


@pytest.fixture()
def two(client):
    """Два клуба. Названия ПК и телефон гостя одинаковые (так можно), в клубе Б уже идёт сессия."""
    a, b = make_club(client, "club-a", PHONE_A), make_club(client, "club-b", PHONE_B)
    a["pc"], b["pc"] = make_pc(client, a, "PC-1"), make_pc(client, b, "PC-1")
    a["tariff"], b["tariff"] = make_tariff(client, a, name="Час A"), make_tariff(client, b, name="Час B")
    a["guest"] = make_guest(client, a, a["pc"], GUEST1, balance=1000)
    b["guest"] = make_guest(client, b, b["pc"], GUEST1, balance=1000)
    r = start_for(client, b, b["guest"], b["tariff"])
    assert r.status_code == 200
    b["session"] = r.json()
    return client, a, b


def test_lists_show_only_own_data(two):
    client, a, b = two
    assert [p["id"] for p in client.get("/api/admin/pcs", headers=a["staff"]).json()] == [a["pc"]["id"]]
    assert [t["name"] for t in client.get("/api/admin/tariffs", headers=a["staff"]).json()] == ["Час A"]
    assert client.get("/api/admin/sessions", headers=a["staff"]).json() == []
    assert len(client.get("/api/admin/sessions", headers=b["staff"]).json()) == 1
    assert [x["id"] for x in client.get("/api/admin/accounts", headers=a["staff"]).json()] == [a["guest"]["id"]]


def test_cannot_use_foreign_pc_tariff_or_guest(two):
    client, a, b = two
    body = lambda **k: {"pc_id": a["pc"]["id"], "tariff_id": a["tariff"]["id"], "account_id": a["guest"]["id"], **k}
    post = lambda **k: client.post("/api/admin/sessions", headers=a["staff"], json=body(**k)).status_code
    assert post(pc_id=b["pc"]["id"]) == 404
    assert post(tariff_id=b["tariff"]["id"]) == 404
    assert post(account_id=b["guest"]["id"]) == 404
    assert state(client, a["pc"])["mode"] == "locked"


def test_cannot_touch_foreign_accounts(two):
    client, a, b = two
    gid = b["guest"]["id"]
    assert client.post(f"/api/admin/accounts/{gid}/topup", json={"amount": 100}, headers=a["staff"]).status_code == 404
    assert client.post(f"/api/admin/accounts/{gid}/adjust", json={"amount": -100, "comment": "x"},
                       headers=a["staff"]).status_code == 404
    assert client.post(f"/api/admin/accounts/{gid}/block", headers=a["staff"]).status_code == 404
    assert client.get(f"/api/admin/accounts/{gid}/transactions", headers=a["staff"]).status_code == 404
    b_account = client.get("/api/admin/accounts", headers=b["staff"]).json()[0]  # данные Б не тронуты
    assert b_account["balance"] == 1000 and b_account["status"] == "active"
    assert state(client, b["pc"])["mode"] == "guest_session"


def test_cannot_stop_foreign_session(two):
    client, a, b = two
    r = client.post(f"/api/admin/sessions/{b['session']['id']}/stop", headers=a["staff"])
    assert r.status_code == 404
    assert state(client, b["pc"])["mode"] == "guest_session"


def test_cannot_touch_foreign_tariff_or_key(two):
    client, a, b = two
    assert client.delete(f"/api/admin/tariffs/{b['tariff']['id']}", headers=a["staff"]).status_code == 404
    assert client.post(f"/api/admin/pcs/{b['pc']['id']}/rotate-key", headers=a["staff"]).status_code == 404
    assert client.post(f"/api/admin/pcs/{b['pc']['id']}/lock", headers=a["staff"]).status_code == 404
    assert [t["name"] for t in client.get("/api/admin/tariffs", headers=b["staff"]).json()] == ["Час B"]
    assert client.get("/api/agent/state", headers=b["pc"]["headers"]).status_code == 200  # ключ Б цел


def test_revenue_report_and_audit_are_per_club(two):
    client, a, b = two
    client.post(f"/api/admin/sessions/{b['session']['id']}/stop", headers=b["staff"])
    since = "2000-01-01T00:00:00"
    assert client.get("/api/admin/report", params={"since": since}, headers=b["staff"]).json()["sessions"] == 1
    assert client.get("/api/admin/report", params={"since": since}, headers=a["staff"]).json() == {
        "sessions": 0, "revenue": 0}
    assert {r["id"] for r in client.get("/api/admin/audit", headers=a["staff"]).json()}.isdisjoint(
        {r["id"] for r in client.get("/api/admin/audit", headers=b["staff"]).json()})


def test_pc_key_sees_only_its_club(two):
    client, a, b = two
    assert state(client, a["pc"])["mode"] == "locked"
    assert [t["name"] for t in client.get("/api/agent/tariffs", headers=a["pc"]["headers"]).json()] == ["Час A"]
    assert [t["name"] for t in client.get("/api/agent/tariffs", headers=b["pc"]["headers"]).json()] == ["Час B"]


def test_guest_token_works_only_on_its_own_pc(two):
    client, a, b = two
    assert client.get("/api/me", headers=a["guest"]["headers"]).status_code == 200
    stolen = {**a["pc"]["headers"], "Authorization": b["guest"]["headers"]["Authorization"]}  # токен Б + ключ ПК А
    assert client.get("/api/me", headers=stolen).status_code == 401
    other_pc = make_pc(client, a, "PC-2")                                                      # токен А на другом ПК А
    moved = {**other_pc["headers"], "Authorization": a["guest"]["headers"]["Authorization"]}
    assert client.get("/api/me", headers=moved).status_code == 401


def test_same_phone_in_two_clubs_is_independent(two):
    client, a, b = two
    assert a["guest"]["id"] != b["guest"]["id"]
    client.post(f"/api/admin/accounts/{a['guest']['id']}/topup", json={"amount": 50}, headers=a["staff"])
    assert client.get("/api/me", headers=a["guest"]["headers"]).json()["balance"] == 1050
    assert client.get("/api/me", headers=b["guest"]["headers"]).json()["balance"] == 1000  # у Б баланс не изменился


def test_credentials_are_not_interchangeable(two):
    client, a, b = two
    pc_key, token = a["pc"]["headers"], a["staff"]
    assert client.get("/api/admin/pcs", headers=pc_key).status_code == 401              # ключ ПК — не админ
    assert client.get("/api/agent/state", headers=token).status_code == 401             # токен — не ПК
    assert client.get("/api/admin/pcs", headers={"X-Platform-Key": "platform-dev-key"}).status_code == 401
    assert client.get("/api/platform/clubs", headers=token).status_code == 401           # токен — не платформа
    assert client.get("/api/agent/state", headers={"X-PC-Key": "nope"}).status_code == 401
    assert client.get("/api/admin/pcs", headers=a["guest"]["headers"]).status_code == 403  # гость — не админ
    assert client.get("/api/me", headers={**pc_key, **token}).status_code == 401         # токен админки не годится на ПК


def test_rotated_key_replaces_old_one(two):
    client, a, _ = two
    new = client.post(f"/api/admin/pcs/{a['pc']['id']}/rotate-key", headers=a["staff"]).json()["key"]
    assert client.get("/api/agent/state", headers=a["pc"]["headers"]).status_code == 401
    assert client.get("/api/agent/state", headers={"X-PC-Key": new}).status_code == 200


def test_same_staff_phone_in_two_clubs_is_independent(client):
    make_club(client, "club-a", PHONE_A, "password-for-a-1")
    make_club(client, "club-b", PHONE_A, "password-for-b-1")  # тот же номер, другой пароль
    wrong = client.post("/api/admin/auth/login",
                        json={"club": "club-a", "phone": PHONE_A, "password": "password-for-b-1"})
    assert wrong.status_code == 401
    staff_login(client, "club-a", PHONE_A, "password-for-a-1")
    staff_login(client, "club-b", PHONE_A, "password-for-b-1")


def test_phone_unique_inside_club_only(client):
    make_club(client, "club-a", PHONE_A)
    make_club(client, "club-b", PHONE_B)
    with SessionLocal() as db:
        club_a, club_b = db.scalars(select(Club).order_by(Club.id)).all()
        with pytest.raises(ServiceError) as e:
            accounts.create_account(db, club_a.id, PHONE_A, PASSWORD, "guest", utcnow())
        assert e.value.status == 409
        accounts.create_account(db, club_b.id, PHONE_A, PASSWORD, "guest", utcnow())  # в другом клубе можно


def test_db_forbids_two_active_sessions_on_one_pc_or_guest(two):
    """Защита на уровне БД на случай, когда два запроса «стартовать» придут одновременно."""
    client, a, b = two
    with SessionLocal() as db:
        db.add(Session(club_id=b["id"], pc_id=b["pc"]["id"], tariff_id=b["tariff"]["id"],
                       account_id=a["guest"]["id"], started_at=utcnow()))  # тот же ПК, другой гость
        with pytest.raises(IntegrityError):
            db.commit()
    other_pc = make_pc(client, b, "PC-2")
    with SessionLocal() as db:
        db.add(Session(club_id=b["id"], pc_id=other_pc["id"], tariff_id=b["tariff"]["id"],
                       account_id=b["guest"]["id"], started_at=utcnow()))  # тот же гость, другой ПК
        with pytest.raises(IntegrityError):
            db.commit()
