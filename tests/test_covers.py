"""Обложки игр в меню: ручная загрузка, подтягивание из Steam (скачивание подменено в тестах), выдача ПК, изоляция клубов, копирование каталога."""
import io
import urllib.error

import pytest

from app.db import SessionLocal
from app.models import AppCover
from app.services import covers

from .helpers import PHONE_B, PLATFORM, make_club, make_pc
from .test_ads import jpeg, png

DOTA = {"title": "Dota 2", "tab": "games", "category": "MOBA", "age_rating": 12, "launch_kind": "steam", "launch_target": "570"}
CS2 = {"title": "Counter-Strike 2", "tab": "games", "category": "Шутеры", "age_rating": 16, "launch_kind": "steam", "launch_target": "730"}
CHROME = {"title": "Chrome", "tab": "apps", "category": "Браузеры", "launch_kind": "exe", "launch_target": r"C:\chrome.exe"}
POSTER = png(600, 900)


@pytest.fixture()
def env(client):
    club = make_club(client)
    pc = make_pc(client, club)
    return client, club, pc


def add(client, club, body):
    r = client.post("/api/admin/apps", json=body, headers=club["staff"])
    assert r.status_code == 200, r.text
    return r.json()


def upload(client, club, app_id, data, content_type="image/png"):
    return client.put(f"/api/admin/apps/{app_id}/cover", content=data, headers={**club["staff"], "Content-Type": content_type})


def admin_app(client, club, app_id):
    return next(a for a in client.get("/api/admin/apps", headers=club["staff"]).json() if a["id"] == app_id)


# ---------- ручная загрузка ----------
def test_staff_uploads_a_cover_and_the_pc_sees_its_version_and_can_download_it(env):
    client, club, pc = env
    dota = add(client, club, DOTA)
    assert dota["cover_version"] is None
    r = upload(client, club, dota["id"], POSTER)
    assert r.status_code == 200, r.text
    out = r.json()
    assert out["cover_version"] and out["cover_source"] == "manual"
    assert admin_app(client, club, dota["id"])["cover_version"] == out["cover_version"]

    seen = client.get("/api/agent/apps", headers=pc["headers"]).json()
    assert seen[0]["cover_version"] == out["cover_version"]
    assert "cover_source" not in seen[0]                                   # служебное ПК не отдаём
    got = client.get(f"/api/agent/apps/{dota['id']}/cover", headers=pc["headers"])
    assert got.status_code == 200 and got.content == POSTER
    assert got.headers["content-type"] == "image/png" and got.headers["etag"] == f'"{out["cover_version"]}"'
    assert client.get(f"/api/admin/apps/{dota['id']}/cover", headers=club["staff"]).content == POSTER


def test_replacing_changes_the_version_and_deleting_removes_the_cover(env):
    client, club, pc = env
    dota = add(client, club, DOTA)
    first = upload(client, club, dota["id"], POSTER).json()["cover_version"]
    second = upload(client, club, dota["id"], jpeg(600, 900), "image/jpeg").json()["cover_version"]
    assert first != second
    assert client.get(f"/api/agent/apps/{dota['id']}/cover", headers=pc["headers"]).headers["content-type"] == "image/jpeg"

    assert client.delete(f"/api/admin/apps/{dota['id']}/cover", headers=club["staff"]).json() == {"ok": True}
    assert admin_app(client, club, dota["id"])["cover_version"] is None
    assert client.get(f"/api/agent/apps/{dota['id']}/cover", headers=pc["headers"]).status_code == 404
    assert client.delete(f"/api/admin/apps/{dota['id']}/cover", headers=club["staff"]).status_code == 404


@pytest.mark.parametrize("data, content_type, status", [
    (b"", "image/png", 422),                                               # пусто
    (b"GIF89a" + b"\x00" * 200, "image/gif", 415),                         # не PNG и не JPEG
    (png(40, 40), "image/png", 422),                                       # меньше 48 пикселей
    (png(5000, 400), "image/png", 422),                                    # больше 4096
    (b"\x89PNG\r\n\x1a\n" + b"junk", "image/png", 422),                    # повреждён
])
def test_files_that_are_not_proper_pictures_are_refused_and_nothing_is_stored(env, data, content_type, status):
    client, club, pc = env
    dota = add(client, club, DOTA)
    r = upload(client, club, dota["id"], data, content_type)
    assert r.status_code == status, r.text
    assert admin_app(client, club, dota["id"])["cover_version"] is None


def test_a_file_over_two_megabytes_is_refused_before_it_is_read(env):
    client, club, pc = env
    dota = add(client, club, DOTA)
    assert upload(client, club, dota["id"], png(600, 900, extra=b"x" * (2 * 1024 * 1024 + 1))).status_code == 413


# ---------- изоляция ----------
def test_other_clubs_and_hidden_games_do_not_leak_covers(client):
    a = make_club(client)
    pc_a = make_pc(client, a)
    b = make_club(client, slug="club-b", phone=PHONE_B)
    pc_b = make_pc(client, b)
    dota = add(client, a, DOTA)
    upload(client, a, dota["id"], POSTER)

    assert client.get(f"/api/agent/apps/{dota['id']}/cover", headers=pc_b["headers"]).status_code == 404       # чужой клуб
    assert upload(client, b, dota["id"], POSTER).status_code == 404
    assert client.get(f"/api/admin/apps/{dota['id']}/cover", headers=b["staff"]).status_code == 404
    assert client.delete(f"/api/admin/apps/{dota['id']}/cover", headers=b["staff"]).status_code == 404

    client.patch(f"/api/admin/apps/{dota['id']}", json={"active": False}, headers=a["staff"])
    assert client.get(f"/api/agent/apps/{dota['id']}/cover", headers=pc_a["headers"]).status_code == 404       # скрытую игру ПК не скачает
    assert client.get(f"/api/admin/apps/{dota['id']}/cover", headers=a["staff"]).status_code == 200            # а админка видит
    assert client.get(f"/api/agent/apps/{dota['id']}/cover").status_code == 401                                 # без ключа ПК нельзя


# ---------- Steam ----------
def test_steam_fills_only_steam_games_without_a_cover_and_never_replaces_a_manual_one(env, monkeypatch):
    client, club, pc = env
    dota, cs2, chrome = add(client, club, DOTA), add(client, club, CS2), add(client, club, CHROME)
    mine = upload(client, club, cs2["id"], png(600, 900, extra=b"")).json()["cover_version"]
    asked = []

    def fake(steam_id):
        asked.append(steam_id)
        return png(600, 900) if steam_id == "570" else None

    monkeypatch.setattr(covers, "fetch_steam_cover", fake)
    r = client.post("/api/admin/apps/covers/steam", headers=club["staff"])
    assert r.status_code == 200, r.text
    assert r.json() == {"added": 1, "not_found": 0, "left": 0}
    assert asked == ["570"]                                                # у CS2 уже своя обложка, у Chrome нет номера Steam
    assert admin_app(client, club, dota["id"])["cover_source"] == "steam"
    assert admin_app(client, club, cs2["id"])["cover_version"] == mine and admin_app(client, club, cs2["id"])["cover_source"] == "manual"
    assert admin_app(client, club, chrome["id"])["cover_version"] is None


def test_steam_reports_games_it_has_no_picture_for_and_tries_them_again_next_time(env, monkeypatch):
    client, club, pc = env
    dota = add(client, club, DOTA)
    monkeypatch.setattr(covers, "fetch_steam_cover", lambda steam_id: None)
    assert client.post("/api/admin/apps/covers/steam", headers=club["staff"]).json() == {"added": 0, "not_found": 1, "left": 0}
    monkeypatch.setattr(covers, "fetch_steam_cover", lambda steam_id: png(600, 900))
    assert client.post("/api/admin/apps/covers/steam", headers=club["staff"]).json() == {"added": 1, "not_found": 0, "left": 0}
    assert admin_app(client, club, dota["id"])["cover_source"] == "steam"
    assert client.post("/api/admin/apps/covers/steam", headers=club["staff"]).json() == {"added": 0, "not_found": 0, "left": 0}   # больше нечего


def test_a_steam_answer_that_is_not_a_picture_is_skipped(env, monkeypatch):
    client, club, pc = env
    dota = add(client, club, DOTA)
    monkeypatch.setattr(covers, "fetch_steam_cover", lambda steam_id: b"<html>not found</html>")
    assert client.post("/api/admin/apps/covers/steam", headers=club["staff"]).json() == {"added": 0, "not_found": 1, "left": 0}
    assert admin_app(client, club, dota["id"])["cover_version"] is None


def test_the_steam_download_is_limited_by_time_and_the_rest_waits_for_the_next_press(env, monkeypatch):
    client, club, pc = env
    for i in range(3):
        add(client, club, {**DOTA, "title": f"Игра {i}", "launch_target": str(1000 + i)})
    clock = {"t": 0.0}
    monkeypatch.setattr(covers.time, "monotonic", lambda: clock["t"])
    def slow(steam_id):
        clock["t"] += covers.STEAM_BATCH_SECONDS / 2 + 1                  # каждая картинка «качается» чуть дольше половины отведённого времени
        return png(600, 900)
    monkeypatch.setattr(covers, "fetch_steam_cover", slow)
    r = client.post("/api/admin/apps/covers/steam", headers=club["staff"]).json()
    assert r["added"] == 2 and r["left"] == 1
    r = client.post("/api/admin/apps/covers/steam", headers=club["staff"]).json()
    assert r == {"added": 1, "not_found": 0, "left": 0}


def test_the_steam_fetcher_builds_a_fixed_address_and_refuses_anything_but_digits(monkeypatch):
    urls = []

    class Reply(io.BytesIO):
        def __enter__(self): return self
        def __exit__(self, *a): return False

    def fake_open(request, timeout):
        urls.append(request.full_url)
        return Reply(b"\x89PNG" + b"x" * 100)

    monkeypatch.setattr(covers.urllib.request, "urlopen", fake_open)
    assert covers.fetch_steam_cover("570") is not None
    assert urls == ["https://cdn.cloudflare.steamstatic.com/steam/apps/570/library_600x900.jpg"]
    for bad in ("../etc/passwd", "570/../1", "57 0", "", "1" * 13, "570?x=1", "abc"):
        assert covers.fetch_steam_cover(bad) is None
    assert urls == ["https://cdn.cloudflare.steamstatic.com/steam/apps/570/library_600x900.jpg"]   # плохие номера в сеть не уходят

    def failing(request, timeout):
        raise urllib.error.URLError("нет сети")

    monkeypatch.setattr(covers.urllib.request, "urlopen", failing)
    assert covers.fetch_steam_cover("570") is None

    monkeypatch.setattr(covers.urllib.request, "urlopen", lambda request, timeout: Reply(b"x" * (2 * 1024 * 1024 + 5)))
    assert covers.fetch_steam_cover("570") is None                        # слишком большой ответ отвергается


# ---------- каталог ----------
def test_copying_the_catalog_takes_the_covers_along_and_replacing_wipes_the_old_ones(client):
    a = make_club(client)
    b = make_club(client, slug="club-b", phone=PHONE_B)
    dota = add(client, a, DOTA)
    upload(client, a, dota["id"], POSTER)
    old = add(client, b, CS2)
    upload(client, b, old["id"], POSTER)

    r = client.post(f"/api/platform/clubs/{b['id']}/copy-apps", json={"from_club_id": a["id"], "replace": True}, headers=PLATFORM)
    assert r.status_code == 200, r.text
    after = client.get("/api/admin/apps", headers=b["staff"]).json()
    assert [x["title"] for x in after] == ["Dota 2"]
    assert after[0]["cover_version"] and after[0]["cover_version"] == admin_app(client, a, dota["id"])["cover_version"]
    assert client.get(f"/api/admin/apps/{after[0]['id']}/cover", headers=b["staff"]).content == POSTER
    with SessionLocal() as db:
        from sqlalchemy import func, select
        from app.models import CatalogApp
        orphans = db.scalar(select(func.count()).select_from(AppCover).where(AppCover.app_id.not_in(select(CatalogApp.id))))
        assert orphans == 0                                              # обложка стёртой игры не осталась без хозяина
        assert db.scalar(select(func.count()).select_from(AppCover)) == 2   # у исходного клуба и у копии
        assert db.get(AppCover, dota["id"]) is not None


def test_deleting_a_game_deletes_its_cover(env):
    client, club, pc = env
    dota = add(client, club, DOTA)
    upload(client, club, dota["id"], POSTER)
    assert client.delete(f"/api/admin/apps/{dota['id']}", headers=club["staff"]).status_code == 200
    with SessionLocal() as db:
        assert db.get(AppCover, dota["id"]) is None


def test_cover_actions_are_written_to_the_audit_log(env, monkeypatch):
    client, club, pc = env
    dota = add(client, club, DOTA)
    upload(client, club, dota["id"], POSTER)
    monkeypatch.setattr(covers, "fetch_steam_cover", lambda steam_id: None)
    client.post("/api/admin/apps/covers/steam", headers=club["staff"])
    client.delete(f"/api/admin/apps/{dota['id']}/cover", headers=club["staff"])
    actions = [r["action"] for r in client.get("/api/admin/audit", headers=club["staff"]).json()]
    assert actions[:3] == ["app_cover_delete", "covers_steam", "app_cover_set"]


def test_a_program_icon_of_96_pixels_is_accepted_as_the_cover(env):
    """Иконки программ с ПК мельче постеров игр: нижняя граница для обложек 48 пикселей (для рекламных баннеров осталось 100)."""
    client, club, pc = env
    chrome = add(client, club, CHROME)
    assert upload(client, club, chrome["id"], png(96, 96)).status_code == 200
    assert admin_app(client, club, chrome["id"])["cover_version"]
