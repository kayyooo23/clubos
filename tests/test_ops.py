"""Состояние сервера: проверка «жив ли» для автоматики и подробное состояние для владельца платформы."""
from datetime import datetime, timedelta

import pytest
from sqlalchemy import text

from app import config
from app.db import engine, get_db
from app.main import app
from app.services import backups, ops

from .conftest import ON_POSTGRES
from .helpers import PLATFORM, make_club, make_pc, raw_state


@pytest.fixture()
def alembic_table():
    """Таблица версий миграций (в тестах схема создаётся из моделей, а на боевом сервере её ведёт alembic)."""
    with engine.begin() as c:
        c.execute(text("drop table if exists alembic_version"))
        c.execute(text("create table alembic_version (version_num varchar(32) not null)"))
    yield
    with engine.begin() as c:
        c.execute(text("drop table if exists alembic_version"))


def set_revision(rev):
    with engine.begin() as c:
        c.execute(text("delete from alembic_version"))
        c.execute(text("insert into alembic_version (version_num) values (:r)"), {"r": rev})


# ---------- /api/health ----------
def test_health_is_public_and_says_nothing_about_clubs(client):
    make_club(client)
    r = client.get("/api/health")
    assert r.status_code == 200
    assert r.json() == {"ok": True, "db": True, "schema": "unknown"}                   # схема в тестах создана из моделей: версии миграций нет, это не ошибка
    assert r.headers["cache-control"] == "no-store"


def test_health_reports_an_outdated_schema_with_503_so_that_a_forgotten_migration_is_noticed(client, alembic_table):
    set_revision(ops.schema_head())
    ok = client.get("/api/health")
    assert ok.status_code == 200 and ok.json()["schema"] == "ok"
    set_revision("0001")
    old = client.get("/api/health")
    assert old.status_code == 503 and old.json() == {"ok": False, "db": True, "schema": "outdated"}


def test_health_says_503_when_the_database_does_not_answer(client):
    class Broken:
        def execute(self, *_):
            raise RuntimeError("база недоступна")

        def rollback(self):
            pass

    app.dependency_overrides[get_db] = lambda: Broken()
    try:
        r = client.get("/api/health")
    finally:
        app.dependency_overrides.pop(get_db, None)
    assert r.status_code == 503 and r.json() == {"ok": False, "db": False, "schema": "unknown"}


# ---------- /api/platform/status ----------
def test_the_platform_status_needs_the_platform_key(client):
    club = make_club(client)
    assert client.get("/api/platform/status").status_code == 401
    assert client.get("/api/platform/status", headers=club["staff"]).status_code == 401


def test_the_platform_status_counts_clubs_pcs_and_sessions(client, tmp_path, monkeypatch):
    monkeypatch.setattr(config, "BACKUP_DIR", str(tmp_path / "none"))
    club = make_club(client)
    on, off = make_pc(client, club, "ON"), make_pc(client, club, "OFF")
    raw_state(client, on)                                                               # только этот ПК «на связи»
    make_club(client, slug="club-b", phone="+79990000002")
    s = client.get("/api/platform/status", headers=PLATFORM).json()
    assert s["clubs"] == {"total": 2, "active": 2}
    assert s["pcs"] == {"total": 2, "online": 1}
    assert s["sessions_active"] == 0
    assert s["db"]["kind"] == ("postgres" if ON_POSTGRES else "sqlite") and (s["db"]["size_bytes"] or 0) > 0
    assert s["health"]["ok"] is True and s["schema"]["state"] == "unknown" and s["uptime_seconds"] >= 0 and s["env"]


def test_the_status_warns_about_missing_stale_or_failed_backups(client, tmp_path, monkeypatch):
    monkeypatch.setattr(config, "BACKUP_DIR", str(tmp_path / "b"))
    now = datetime(2026, 10, 13, 12, 0, 0)
    assert "Резервных копий нет" in ops.backups_info(now)["warning"]
    dest = tmp_path / "b"
    dest.mkdir()
    (dest / backups.new_name(now - timedelta(hours=3), "sqlite")).write_bytes(b"x")
    fresh = ops.backups_info(now)
    assert fresh["warning"] is None and fresh["age_hours"] == 3.0 and len(fresh["files"]) == 1 and fresh["disk_free"] > 0
    (dest / backups.new_name(now - timedelta(hours=40), "sqlite")).write_bytes(b"x")
    assert ops.backups_info(now)["files"][0]["name"] == backups.new_name(now - timedelta(hours=3), "sqlite")          # свежие впереди
    stale = ops.backups_info(now + timedelta(hours=30))
    assert "33 ч назад" in stale["warning"]                                              # новой копии давно нет
    backups.write_status(dest, now, False, "диск переполнен")
    failed = ops.backups_info(now)
    assert "неудачна" in failed["warning"] and "диск переполнен" in failed["warning"]
    backups.write_status(dest, now, True, "Копия сделана и проверена")
    assert ops.backups_info(now)["warning"] is None
    through_api = client.get("/api/platform/status", headers=PLATFORM).json()["backups"]
    assert through_api["dir"] == str(dest) and len(through_api["files"]) == 2
