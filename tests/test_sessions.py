from datetime import datetime, timedelta

from app.db import SessionLocal
from app.models import Session

from .helpers import GUEST1, PHONE_B, make_club, make_guest, make_pc, make_tariff, start_for, state


def test_lock_flow(client):
    club = make_club(client)
    pc, hourly = make_pc(client, club), make_tariff(client, club)
    guest = make_guest(client, club, pc, balance=500)
    assert state(client, pc)["mode"] == "locked"  # гость в кабинете, но ПК ещё заблокирован
    s = start_for(client, club, guest, hourly).json()
    assert state(client, pc)["mode"] == "guest_session"
    assert start_for(client, club, guest, hourly).status_code == 409  # второй раз на тот же ПК нельзя
    done = client.post(f"/api/admin/sessions/{s['id']}/stop", headers=club["staff"]).json()
    assert done["ended_at"] is not None and done["cost"] >= 0
    assert state(client, pc)["mode"] == "locked"


def test_package_expires_and_locks(client):
    club = make_club(client)
    pc = make_pc(client, club)
    pkg = make_tariff(client, club, name="3 часа", kind="package", price=250, package_minutes=180)
    guest = make_guest(client, club, pc, balance=1000)
    s = start_for(client, club, guest, pkg).json()
    with SessionLocal() as db:  # сдвигаем старт назад на 4 часа — пакет должен истечь
        row = db.get(Session, s["id"])
        row.started_at -= timedelta(hours=4)
        row.planned_end -= timedelta(hours=4)
        db.commit()
    assert state(client, pc)["mode"] == "locked"
    assert client.get("/api/admin/sessions", headers=club["staff"]).json()[0]["cost"] == 250
    assert client.get("/api/admin/accounts", headers=club["staff"]).json()[0]["balance"] == 750


def test_night_tariff_follows_club_timezone(client, monkeypatch):
    moment = datetime(2026, 1, 1, 20, 30)  # 23:30 в Москве, 20:30 на UTC
    monkeypatch.setattr("app.routers.admin.utcnow", lambda: moment)
    monkeypatch.setattr("app.routers.auth.utcnow", lambda: moment)
    msk, utc = make_club(client, "club-msk", utc_offset=3), make_club(client, "club-utc", PHONE_B, utc_offset=0)
    results = {}
    for club in (msk, utc):
        pc = make_pc(client, club)
        night = make_tariff(client, club, name="Ночь", kind="night", price=400, night_start=23, night_end=8)
        guest = make_guest(client, club, pc, balance=1000)
        results[club["slug"]] = start_for(client, club, guest, night)
    assert results["club-msk"].status_code == 200
    assert results["club-msk"].json()["planned_end"].startswith("2026-01-02T05:00")  # 08:00 МСК
    assert results["club-utc"].status_code == 400


def test_inactive_tariff_cannot_start(client):
    club = make_club(client)
    pc, t = make_pc(client, club), make_tariff(client, club)
    guest = make_guest(client, club, pc, balance=500)
    client.delete(f"/api/admin/tariffs/{t['id']}", headers=club["staff"])
    assert start_for(client, club, guest, t).status_code == 404


def test_pc_name_unique_inside_club(client):
    club = make_club(client)
    make_pc(client, club, "PC-1")
    assert client.post("/api/admin/pcs", json={"name": "PC-1"}, headers=club["staff"]).status_code == 409


def test_heartbeat_marks_pc_online(client):
    club = make_club(client)
    pc = make_pc(client, club)
    assert client.get("/api/admin/pcs", headers=club["staff"]).json()[0]["online"] is False
    state(client, pc)
    assert client.get("/api/admin/pcs", headers=club["staff"]).json()[0]["online"] is True


def test_report_accepts_timezone_aware_dates(client):
    club = make_club(client)
    pc, t = make_pc(client, club), make_tariff(client, club)
    guest = make_guest(client, club, pc, balance=500)
    s = start_for(client, club, guest, t).json()
    client.post(f"/api/admin/sessions/{s['id']}/stop", headers=club["staff"])
    r = client.get("/api/admin/report", params={"since": "2000-01-01T00:00:00+03:00"}, headers=club["staff"])
    assert r.status_code == 200 and r.json()["sessions"] == 1


def test_tariff_validation(client):
    club = make_club(client)
    post = lambda body: client.post("/api/admin/tariffs", json=body, headers=club["staff"]).status_code
    assert post({"name": "x", "kind": "package", "price": 100}) == 422                       # нет package_minutes
    assert post({"name": "x", "kind": "night", "price": 100}) == 422                         # нет окна
    assert post({"name": "x", "kind": "night", "price": 100, "night_start": 5, "night_end": 5}) == 422
    assert post({"name": "x", "kind": "hourly", "price": 0}) == 422


def test_session_requires_existing_guest(client):
    club = make_club(client)
    pc, t = make_pc(client, club), make_tariff(client, club)
    r = client.post("/api/admin/sessions", headers=club["staff"], json={"pc_id": pc["id"], "tariff_id": t["id"]})
    assert r.status_code == 422                                                              # account_id обязателен
    r = client.post("/api/admin/sessions", headers=club["staff"],
                    json={"pc_id": pc["id"], "tariff_id": t["id"], "account_id": 999})
    assert r.status_code == 404
    staff_id = client.get("/api/admin/audit", headers=club["staff"]).json()[0]["actor_account_id"]
    r = client.post("/api/admin/sessions", headers=club["staff"],                            # сотрудник — не гость
                    json={"pc_id": pc["id"], "tariff_id": t["id"], "account_id": staff_id})
    assert r.status_code == 404
