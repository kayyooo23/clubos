"""Автообновление клиента, сторона сервера: версии, загрузка пакета, кому и когда они раздаются, итоги обновлений.
Подпись пакета проверяет клиент (сервер её не расшифровывает), поэтому здесь подпись лишь строка нужного вида."""
import base64
import hashlib
import os

import pytest

from app.db import SessionLocal
from app.models import ClientRelease
from app.services import releases

from .helpers import PHONE_B, PLATFORM, make_club, make_pc

SIGNATURE = base64.b64encode(os.urandom(64)).decode()


def package(body: bytes = b"PK\x03\x04 pretend zip " + b"x" * 5000) -> dict:
    return {"bytes": body, "sha256": hashlib.sha256(body).hexdigest(), "size": len(body)}


def new_release(client, version="1.1.0", pkg=None, **over):
    pkg = pkg or package()
    body = {"version": version, "sha256": pkg["sha256"], "size": pkg["size"], "signature": SIGNATURE, **over}
    r = client.post("/api/platform/releases", json=body, headers=PLATFORM)
    assert r.status_code == 200, r.text
    return {**r.json(), "pkg": pkg}


def upload(client, rel, data=None, **kw):
    return client.put(f"/api/platform/releases/{rel['id']}/package", content=data if data is not None else rel["pkg"]["bytes"], headers=PLATFORM, **kw)


def publish(client, version="1.1.0", percent=100, **over):
    """Версия заведена, пакет загружен, версия включена."""
    rel = new_release(client, version, **over)
    assert upload(client, rel).status_code == 200
    r = client.patch(f"/api/platform/releases/{rel['id']}", json={"active": True, "rollout_percent": percent}, headers=PLATFORM)
    assert r.status_code == 200, r.text
    return rel


def check(client, pc, current="1.0.0"):
    r = client.get("/api/agent/update", params={"current": current}, headers=pc["headers"])
    assert r.status_code == 200, r.text
    return r.json()["update"]


@pytest.fixture()
def env(client):
    club = make_club(client)
    return client, club, make_pc(client, club, "PC-1")


# ---------- платформа: версии ----------
def test_only_the_platform_manages_releases(env):
    client, club, pc = env
    body = {"version": "1.1.0", **{k: v for k, v in package().items() if k != "bytes"}, "signature": SIGNATURE}
    assert client.post("/api/platform/releases", json=body).status_code == 401
    assert client.post("/api/platform/releases", json=body, headers=club["staff"]).status_code == 401
    assert client.get("/api/platform/releases", headers=pc["headers"]).status_code == 401


def test_a_release_is_validated_and_versions_are_unique(env):
    client, _, _ = env
    pkg = package()
    ok = {"version": "1.1.0", "sha256": pkg["sha256"], "size": pkg["size"], "signature": SIGNATURE}
    assert client.post("/api/platform/releases", json=ok, headers=PLATFORM).status_code == 200
    assert client.post("/api/platform/releases", json=ok, headers=PLATFORM).status_code == 409           # номер уже занят
    for bad in ({"version": "1.1"}, {"version": "v1.1.0"}, {"version": "1.1.0; calc"}, {"version": "2.0.0", "sha256": "ABC"},
                {"version": "2.0.0", "size": 0}, {"version": "2.0.0", "signature": "short"}, {"version": "2.0.0", "signature": "не base64!" * 6},
                {"version": "2.0.0", "rollout_percent": 101}, {"version": "2.0.0", "club_ids": [99999]}):
        r = client.post("/api/platform/releases", json={**ok, **bad}, headers=PLATFORM)
        assert r.status_code in (422, 409), (bad, r.status_code)
        assert r.status_code != 409 or bad.get("version") == "1.1.0"


def test_versions_compare_as_numbers():
    assert releases.version_key("1.10.0") > releases.version_key("1.9.0")
    assert releases.version_key("1.2.0") == releases.version_key("1.2.0.0")
    assert releases.version_key("2.0.0") > releases.version_key("1.99.99.99")
    assert releases.valid_version("1.2.3.4") and not releases.valid_version("1.2") and not releases.valid_version("1.2.3.4.5") and not releases.valid_version(None)


# ---------- загрузка пакета ----------
def test_the_package_is_checked_against_what_was_declared_and_stored_on_disk(env):
    client, _, _ = env
    rel = new_release(client)
    assert rel["uploaded"] is False
    r = upload(client, rel)
    assert r.status_code == 200 and r.json()["uploaded"] is True
    with SessionLocal() as db:
        stored = releases.final_path(db.get(ClientRelease, rel["id"]))
    assert stored.read_bytes() == rel["pkg"]["bytes"]
    assert not stored.with_name(stored.name + ".part").exists()


def test_a_wrong_or_oversized_package_is_refused_and_leaves_nothing_behind(env):
    client, _, _ = env
    rel = new_release(client)
    other = package(b"PK other content " + b"y" * 5000)                               # тот же размер? нет: подберём тот же размер, но другое содержимое
    same_size = b"z" * rel["pkg"]["size"]
    assert upload(client, rel, same_size).status_code == 422                          # размер сошёлся, отпечаток нет
    assert upload(client, rel, other["bytes"]).status_code == 422                     # размер не тот
    assert upload(client, rel, rel["pkg"]["bytes"] + b"extra").status_code == 422     # больше заявленного
    with SessionLocal() as db:
        r = db.get(ClientRelease, rel["id"])
        assert r.file_name is None
        assert not releases.final_path(r).exists() and not releases.part_path(r).exists()
    assert upload(client, rel).status_code == 200                                     # после неудач настоящий пакет ложится как ни в чём не бывало


def test_a_released_package_cannot_be_replaced(env):
    client, _, _ = env
    rel = new_release(client)
    assert upload(client, rel).status_code == 200
    assert upload(client, rel).status_code == 409


def test_a_release_cannot_be_enabled_before_its_package_is_uploaded(env):
    client, _, _ = env
    rel = new_release(client)
    assert client.patch(f"/api/platform/releases/{rel['id']}", json={"active": True}, headers=PLATFORM).status_code == 409


# ---------- кому отдаётся ----------
def test_nothing_is_offered_until_the_release_is_enabled(env):
    client, _, pc = env
    rel = new_release(client)
    assert check(client, pc) is None                                                  # метаданные есть, пакета нет
    upload(client, rel)
    assert check(client, pc) is None                                                  # пакет есть, версия не включена
    client.patch(f"/api/platform/releases/{rel['id']}", json={"active": True, "rollout_percent": 100}, headers=PLATFORM)
    offer = check(client, pc)
    assert offer["version"] == "1.1.0" and offer["sha256"] == rel["pkg"]["sha256"] and offer["size"] == rel["pkg"]["size"] and offer["signature"] == SIGNATURE
    assert offer["url"] == f"/api/agent/update/package/{rel['id']}"
    client.patch(f"/api/platform/releases/{rel['id']}", json={"active": False}, headers=PLATFORM)
    assert check(client, pc) is None                                                  # выключили: больше не раздаётся


def test_only_a_newer_version_is_offered_and_the_newest_wins(env):
    client, _, pc = env
    publish(client, "1.1.0")
    publish(client, "1.10.0", pkg=package(b"PK second " + b"q" * 3000))
    publish(client, "1.9.0", pkg=package(b"PK third " + b"w" * 3000))
    assert check(client, pc, "1.0.0")["version"] == "1.10.0"                          # «1.10» новее «1.9», сравниваются числа
    assert check(client, pc, "1.9.0")["version"] == "1.10.0"
    assert check(client, pc, "1.10.0") is None                                        # уже стоит самая новая
    assert check(client, pc, "2.0.0") is None                                         # назад сервер не откатывает


def test_a_gradual_rollout_gives_the_version_to_a_stable_share_of_pcs(client):
    club = make_club(client)
    pcs = [make_pc(client, club, f"PC-{i}") for i in range(40)]
    rel = publish(client, "1.1.0", percent=0)
    assert all(check(client, pc) is None for pc in pcs)                               # 0 процентов: никому
    client.patch(f"/api/platform/releases/{rel['id']}", json={"rollout_percent": 50}, headers=PLATFORM)
    first = [bool(check(client, pc)) for pc in pcs]
    assert 5 <= sum(first) <= 35                                                      # примерно половина, но не все и не никто
    assert first == [bool(check(client, pc)) for pc in pcs]                           # каждый раз одним и тем же ПК
    client.patch(f"/api/platform/releases/{rel['id']}", json={"rollout_percent": 100}, headers=PLATFORM)
    assert all(check(client, pc) for pc in pcs)
    client.patch(f"/api/platform/releases/{rel['id']}", json={"rollout_percent": 20}, headers=PLATFORM)
    smaller = [bool(check(client, pc)) for pc in pcs]
    assert sum(smaller) <= sum(first) and all(first[i] for i, got in enumerate(smaller) if got)   # доля растёт по тем же ПК: меньшая доля входит в большую


def test_a_release_can_be_limited_to_chosen_clubs(client):
    mine = make_club(client)
    theirs = make_club(client, slug="club-b", phone=PHONE_B)
    pc_mine, pc_theirs = make_pc(client, mine, "PC-1"), make_pc(client, theirs, "PC-1")
    rel = publish(client, "1.1.0", club_ids=[mine["id"]])
    assert check(client, pc_mine)["version"] == "1.1.0"
    assert check(client, pc_theirs) is None                                           # чужой клуб не получает
    assert client.get(f"/api/agent/update/package/{rel['id']}", headers=pc_theirs["headers"]).status_code == 404
    client.patch(f"/api/platform/releases/{rel['id']}", json={"club_ids": []}, headers=PLATFORM)
    assert check(client, pc_theirs)["version"] == "1.1.0"                             # пустой список: все клубы


# ---------- скачивание ----------
def test_the_package_is_served_only_to_a_pc_it_is_offered_to(env):
    client, _, pc = env
    rel = publish(client, "1.1.0")
    r = client.get(f"/api/agent/update/package/{rel['id']}", headers=pc["headers"])
    assert r.status_code == 200 and r.content == rel["pkg"]["bytes"]
    assert hashlib.sha256(r.content).hexdigest() == rel["pkg"]["sha256"]
    assert client.get(f"/api/agent/update/package/{rel['id']}").status_code == 401   # без ключа ПК нельзя
    part = client.get(f"/api/agent/update/package/{rel['id']}", headers={**pc["headers"], "Range": "bytes=0-9"})
    assert part.status_code == 206 and part.content == rel["pkg"]["bytes"][:10]       # докачка работает
    client.patch(f"/api/platform/releases/{rel['id']}", json={"active": False}, headers=PLATFORM)
    assert client.get(f"/api/agent/update/package/{rel['id']}", headers=pc["headers"]).status_code == 404
    assert client.get("/api/agent/update/package/99999", headers=pc["headers"]).status_code == 404


def test_the_pc_must_state_a_valid_current_version(env):
    client, _, pc = env
    for bad in ("", "1.2", "abc", "1.2.3; drop"):
        assert client.get("/api/agent/update", params={"current": bad}, headers=pc["headers"]).status_code == 422
    assert client.get("/api/agent/update", headers=pc["headers"]).status_code == 422
    assert client.get("/api/agent/update", params={"current": "1.0.0"}).status_code == 401


# ---------- версия и итог на ПК ----------
def test_the_server_remembers_which_version_each_pc_runs_and_how_the_update_went(env):
    client, club, pc = env
    listed = lambda: client.get("/api/admin/pcs", headers=club["staff"]).json()[0]
    assert listed()["client_version"] is None
    check(client, pc, "1.0.0")
    assert listed()["client_version"] == "1.0.0"
    r = client.post("/api/agent/update/result", json={"version": "1.1.0", "status": "rolled_back", "message": "Новая версия не вышла на связь"}, headers=pc["headers"])
    assert r.status_code == 200
    row = listed()
    assert row["update_status"] == "rolled_back" and "не вышла на связь" in row["update_message"] and row["client_version"] == "1.0.0"
    client.post("/api/agent/update/result", json={"version": "1.2.0", "status": "ok"}, headers=pc["headers"])
    row = listed()
    assert row["update_status"] == "ok" and row["client_version"] == "1.2.0"
    actions = [a["action"] for a in client.get("/api/admin/audit", headers=club["staff"]).json()]
    assert "client_update_rolled_back" in actions and "client_update_ok" in actions
    assert client.post("/api/agent/update/result", json={"version": "1.2.0", "status": "great"}, headers=pc["headers"]).status_code == 422


def test_the_platform_sees_how_many_pcs_run_each_version_and_can_delete_only_disabled_ones(env):
    client, club, pc = env
    pc2 = make_pc(client, club, "PC-2")
    rel = publish(client, "1.1.0")
    check(client, pc, "1.1.0")
    check(client, pc2, "1.0.0")
    rows = {r["version"]: r for r in client.get("/api/platform/releases", headers=PLATFORM).json()}
    assert rows["1.1.0"]["pcs"] == 1 and rows["1.1.0"]["active"] is True and rows["1.1.0"]["uploaded"] is True
    assert client.delete(f"/api/platform/releases/{rel['id']}", headers=PLATFORM).status_code == 409   # включена
    client.patch(f"/api/platform/releases/{rel['id']}", json={"active": False}, headers=PLATFORM)
    with SessionLocal() as db:
        stored = releases.final_path(db.get(ClientRelease, rel["id"]))
    assert stored.exists()
    assert client.delete(f"/api/platform/releases/{rel['id']}", headers=PLATFORM).status_code == 200
    assert not stored.exists()
    assert client.get("/api/platform/releases", headers=PLATFORM).json() == []
