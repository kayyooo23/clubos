"""Синхронизация игр, сторона сервера: задания, участники, что видит каждый ПК, ход работы, список игр на ПК. Сами файлы идут между ПК и до сервера не доходят."""
from datetime import timedelta

import pytest

from app.db import SessionLocal
from app.errors import ServiceError
from app.models import SyncJob
from app.services import sync
from app.timeutil import utcnow

from .helpers import GUEST1, GUEST2, PHONE_B, make_club, make_guest, make_pc, make_tariff, raw_state, start_for

SHA = "a" * 64


def new_pc(client, club, name, online=True):
    pc = make_pc(client, club, name)
    if online:
        raw_state(client, pc)                                                           # опрос ПК: он «на связи»
    return pc


@pytest.fixture()
def env(client):
    club = make_club(client)
    source, one, two = (new_pc(client, club, n) for n in ("SRC", "PC-2", "PC-3"))
    return client, club, source, one, two


def start(client, club, source, targets=None, paths=("steamapps/common/Dota 2",), **extra):
    body = {"source_pc_id": source["id"], "paths": list(paths), **extra}
    if targets is not None:
        body["target_pc_ids"] = [t["id"] for t in targets]
    return client.post("/api/admin/sync/jobs", json=body, headers=club["staff"])


def jobs(client, club):
    return client.get("/api/admin/sync/jobs", headers=club["staff"]).json()


def manifest(client, pc, job_id, sha=SHA, files=3, size=12_000_000):
    return client.post(f"/api/agent/sync/{job_id}/manifest", json={"sha256": sha, "files": files, "bytes": size}, headers=pc["headers"])


def progress(client, pc, job_id, status, **fields):
    return client.post(f"/api/agent/sync/{job_id}/progress", json={"status": status, **fields}, headers=pc["headers"])


def assignment(client, pc):
    return raw_state(client, pc).get("sync", [])


# ---------- пути ----------
@pytest.mark.parametrize("raw,clean", [("Games\\Dota 2", "Games/Dota 2"), ("  steamapps/common/CS2/ ", "steamapps/common/CS2"), ("a/b/c", "a/b/c")])
def test_paths_are_normalized_to_forward_slashes(raw, clean):
    assert sync.clean_path(raw) == clean


@pytest.mark.parametrize("raw", ["", "   ", "/etc", "C:/Windows", "a/../b", "..", "a//b", "a/./b", "a:b", "a/b?", 'a/"b"', "a /b", "a/b.", "x" * 261, "a/\x01"])
def test_dangerous_or_malformed_paths_are_refused(raw):
    with pytest.raises(ServiceError) as e:
        sync.clean_path(raw)
    assert e.value.status == 422


def test_duplicate_and_nested_paths_collapse_to_the_outer_one():
    assert sync.clean_paths(["Games/Dota", "games/dota", "Games/Dota/maps", "Other"]) == ["Other", "Games/Dota"]


# ---------- создание задания ----------
def test_a_job_copies_from_the_source_to_every_other_pc_by_default(env):
    client, club, source, one, two = env
    r = start(client, club, source)
    assert r.status_code == 200, r.text
    job = r.json()
    assert job["status"] == "running" and job["source_name"] == "SRC" and job["paths"] == ["steamapps/common/Dota 2"]
    roles = {t["pc_name"]: (t["role"], t["status"]) for t in job["targets"]}
    assert roles == {"SRC": ("source", "pending"), "PC-2": ("target", "pending"), "PC-3": ("target", "pending")}
    assert job["summary"] == {"total": 2, "ready": 0, "failed": 0, "syncing": 0, "fetched": 0, "from_peers": 0}
    assert "sync_start" in [a["action"] for a in client.get("/api/admin/audit", headers=club["staff"]).json()]


def test_by_default_only_pcs_that_are_on_can_receive_but_an_offline_pc_can_be_chosen_explicitly(env):
    client, club, source, one, two = env
    off = new_pc(client, club, "OFF", online=False)
    job = start(client, club, source).json()
    assert job["skipped_offline"] == ["OFF"]
    assert {t["pc_name"] for t in job["targets"]} == {"SRC", "PC-2", "PC-3"}
    client.post(f"/api/admin/sync/jobs/{job['id']}/cancel", headers=club["staff"])
    chosen = start(client, club, source, [off]).json()                                   # явный выбор: ПК начнёт, когда включится
    assert chosen["skipped_offline"] == [] and [t["pc_name"] for t in chosen["targets"] if t["role"] == "target"] == ["OFF"]
    client.post(f"/api/admin/sync/jobs/{chosen['id']}/cancel", headers=club["staff"])
    lonely = make_club(client, slug="club-b", phone=PHONE_B)
    only, sleeping = new_pc(client, lonely, "ONLY"), new_pc(client, lonely, "SLEEP", online=False)
    r = start(client, lonely, only)
    assert r.status_code == 422 and "на связи" in r.json()["detail"]


def test_targets_can_be_chosen_and_the_source_cannot_be_one_of_them(env):
    client, club, source, one, two = env
    job = start(client, club, source, [one]).json()
    assert [t["pc_name"] for t in job["targets"] if t["role"] == "target"] == ["PC-2"]
    cancel = client.post(f"/api/admin/sync/jobs/{job['id']}/cancel", headers=club["staff"])
    assert cancel.status_code == 200
    assert start(client, club, source, [source, one]).status_code == 422


def test_a_job_needs_an_online_source_valid_paths_and_somewhere_to_copy(env):
    client, club, source, one, two = env
    lonely = make_club(client, slug="club-b", phone=PHONE_B)
    only = new_pc(client, lonely, "ONLY")
    assert start(client, lonely, only).status_code == 422                               # других ПК в клубе нет
    offline = new_pc(client, club, "OFF", online=False)
    r = start(client, club, offline, [one])
    assert r.status_code == 409 and "не на связи" in r.json()["detail"]
    for bad in (["../x"], ["C:/Games"], [], ["a"] * 21 and [f"p{i}" for i in range(21)]):
        assert start(client, club, source, paths=bad).status_code == 422, bad
    assert client.post("/api/admin/sync/jobs", json={"source_pc_id": source["id"], "paths": ["a"], "unknown": 1}, headers=club["staff"]).status_code == 422


def test_a_pc_with_a_guest_cannot_be_the_source_because_its_files_may_change_during_the_copy(env):
    client, club, source, one, two = env
    tariff = make_tariff(client, club, name="Час", kind="hourly", price=100)
    guest = make_guest(client, club, source, phone=GUEST1, balance=500)                  # гость вошёл в кабинет за эталонным ПК
    r = start(client, club, source, [one])
    assert r.status_code == 409 and "сидит гость" in r.json()["detail"]
    assert start_for(client, club, guest, tariff).status_code == 200                       # теперь у него идёт сессия
    assert start(client, club, source, [one]).status_code == 409
    other = make_guest(client, club, two, phone=GUEST2, balance=500)                       # гость за получателем эталону не мешает: копирование у него подождёт
    assert other["id"] != guest["id"]
    assert start(client, club, one, [two]).status_code == 200


def test_a_pc_takes_part_in_one_running_job_at_a_time(env):
    client, club, source, one, two = env
    first = start(client, club, source, [one]).json()
    r = start(client, club, two, [one])
    assert r.status_code == 409 and "PC-2" in r.json()["detail"]
    assert start(client, club, source, [two]).status_code == 409                        # эталон тоже занят
    client.post(f"/api/admin/sync/jobs/{first['id']}/cancel", headers=club["staff"])
    assert start(client, club, two, [one]).status_code == 200                          # после отмены ПК свободны


def test_other_clubs_cannot_see_use_or_cancel_our_jobs_and_pcs(env):
    client, club, source, one, two = env
    job = start(client, club, source).json()
    other = make_club(client, slug="club-b", phone=PHONE_B)
    foreign = new_pc(client, other, "X")
    assert jobs(client, other) == []
    assert client.get(f"/api/admin/sync/jobs/{job['id']}", headers=other["staff"]).status_code == 404
    assert client.post(f"/api/admin/sync/jobs/{job['id']}/cancel", headers=other["staff"]).status_code == 404
    assert start(client, other, foreign, [source]).status_code == 404                   # чужие ПК в своё задание не взять
    assert assignment(client, foreign) == []
    assert manifest(client, foreign, job["id"]).status_code == 404
    assert progress(client, foreign, job["id"], "syncing").status_code == 404


def test_only_staff_can_manage_jobs(env):
    client, club, source, one, two = env
    assert client.post("/api/admin/sync/jobs", json={"source_pc_id": source["id"], "paths": ["a"]}).status_code in (401, 403)
    assert client.get("/api/admin/sync/jobs", headers=source["headers"]).status_code in (401, 403)


# ---------- что видит ПК ----------
def test_each_participant_gets_its_part_of_the_job_in_the_state(env):
    client, club, source, one, two = env
    job = start(client, club, source, [one]).json()
    mine = assignment(client, source)
    assert len(mine) == 1 and mine[0]["id"] == job["id"] and mine[0]["role"] == "source"
    assert mine[0]["paths"] == ["steamapps/common/Dota 2"] and mine[0]["manifest_sha256"] is None and mine[0]["delete_extra"] is False
    theirs = assignment(client, one)[0]
    assert theirs["role"] == "target" and theirs["token"] == mine[0]["token"] and len(theirs["token"]) >= 24
    assert "peers" not in theirs                                                         # пока нет списка файлов, делать нечего
    assert assignment(client, two) == []                                                 # непричастному ПК задание не показывается


def test_the_manifest_is_registered_by_the_source_only_and_only_once(env):
    client, club, source, one, two = env
    job = start(client, club, source, [one]).json()
    assert manifest(client, one, job["id"]).status_code == 404                           # получатель не может объявить список
    assert manifest(client, source, job["id"]).status_code == 200
    assert manifest(client, source, job["id"]).status_code == 200                        # повтор того же безвреден
    assert manifest(client, source, job["id"], sha="b" * 64).status_code == 409          # другой список подменить нельзя
    assert manifest(client, source, job["id"], sha="XYZ").status_code == 422
    seen = assignment(client, one)[0]
    assert seen["manifest_sha256"] == SHA and seen["peers"] == []
    row = jobs(client, club)[0]
    assert row["manifest_files"] == 3 and row["manifest_bytes"] == 12_000_000


def test_receivers_learn_about_peers_that_can_serve_files_inside_the_network(env):
    client, club, source, one, two = env
    job = start(client, club, source).json()
    manifest(client, source, job["id"])
    progress(client, source, job["id"], "ready", serve_url="http://192.168.1.10:47900")
    progress(client, one, job["id"], "syncing", serve_url="http://192.168.1.11:47900", bytes_total=100, bytes_done=10)
    peers_for_two = assignment(client, two)[0]["peers"]
    assert {(p["role"], p["url"]) for p in peers_for_two} == {("source", "http://192.168.1.10:47900"), ("target", "http://192.168.1.11:47900")}
    peers_for_one = assignment(client, one)[0]["peers"]
    assert [p["url"] for p in peers_for_one] == ["http://192.168.1.10:47900"]            # себя в списке нет
    progress(client, two, job["id"], "waiting")
    assert len(assignment(client, one)[0]["peers"]) == 1                                  # без адреса раздачи участник в списке не нужен


def test_progress_is_validated_by_role_state_and_address(env):
    client, club, source, one, two = env
    job = start(client, club, source, [one]).json()
    assert progress(client, one, job["id"], "scanning").status_code == 422               # получатель не сканирует
    assert progress(client, source, job["id"], "syncing").status_code == 422             # эталон не копирует
    assert progress(client, one, job["id"], "ready").status_code == 409                  # списка ещё нет: «готово» невозможно
    for url in ("http://8.8.8.8:47900", "http://example.com:47900", "https://192.168.1.1:47900", "http://192.168.1.1", "http://192.168.1.1:99999999"):
        assert progress(client, one, job["id"], "syncing", serve_url=url).status_code == 422, url
    assert progress(client, one, job["id"], "syncing", serve_url="http://10.0.0.5:47900", bytes_done=5, bytes_total=50, files_done=1, files_total=4).status_code == 200
    assert progress(client, one, job["id"], "syncing", bytes_done=-1).status_code == 422
    assert progress(client, one, 99999, "syncing").status_code == 404


def test_the_job_finishes_when_everyone_is_done_and_then_nothing_more_is_sent_to_the_pcs(env):
    client, club, source, one, two = env
    job = start(client, club, source).json()
    manifest(client, source, job["id"])
    progress(client, source, job["id"], "ready", serve_url="http://192.168.1.10:47900")
    progress(client, one, job["id"], "ready", bytes_done=100, bytes_total=100, bytes_fetched=40, bytes_from_peers=15, files_done=3, files_total=3)
    row = jobs(client, club)[0]
    assert row["status"] == "running"                                                     # PC-3 ещё не закончил
    assert assignment(client, one)[0]["status"] == "ready"                                # готовый продолжает раздавать файлы
    progress(client, two, job["id"], "failed", message="Не хватает места на диске")
    row = jobs(client, club)[0]
    assert row["status"] == "done" and row["finished_at"] is not None
    assert row["summary"] == {"total": 2, "ready": 1, "failed": 1, "syncing": 0, "fetched": 40, "from_peers": 15}
    t = {x["pc_name"]: x for x in row["targets"]}
    assert t["PC-2"]["percent"] == 100 and t["PC-3"]["message"] == "Не хватает места на диске"
    for pc in (source, one, two):
        assert assignment(client, pc) == []                                               # задание закрыто: участники прекращают
    assert progress(client, one, job["id"], "syncing").status_code == 409


def test_if_the_source_fails_the_receivers_fail_with_a_clear_reason(env):
    client, club, source, one, two = env
    job = start(client, club, source).json()
    progress(client, source, job["id"], "failed", message="Не найдена папка игры")
    row = jobs(client, club)[0]
    assert row["status"] == "done"
    assert {t["pc_name"]: t["status"] for t in row["targets"]} == {"SRC": "failed", "PC-2": "failed", "PC-3": "failed"}
    assert "Эталонный ПК" in next(t for t in row["targets"] if t["pc_name"] == "PC-2")["message"]


def test_cancelling_stops_everyone_and_frees_the_pcs(env):
    client, club, source, one, two = env
    job = start(client, club, source).json()
    manifest(client, source, job["id"])
    progress(client, one, job["id"], "syncing", bytes_done=10, bytes_total=100)
    r = client.post(f"/api/admin/sync/jobs/{job['id']}/cancel", headers=club["staff"])
    assert r.status_code == 200 and r.json()["status"] == "cancelled"
    assert {t["pc_name"]: t["status"] for t in r.json()["targets"]} == {"SRC": "cancelled", "PC-2": "cancelled", "PC-3": "cancelled"}
    assert all(assignment(client, pc) == [] for pc in (source, one, two))
    assert progress(client, one, job["id"], "syncing").status_code == 409
    assert client.post(f"/api/admin/sync/jobs/{job['id']}/cancel", headers=club["staff"]).status_code == 409
    assert "sync_cancel" in [a["action"] for a in client.get("/api/admin/audit", headers=club["staff"]).json()]


def test_a_job_that_runs_too_long_is_cancelled_by_itself(env):
    client, club, source, one, two = env
    job = start(client, club, source).json()
    with SessionLocal() as db:
        db.get(SyncJob, job["id"]).created_at = utcnow() - timedelta(hours=sync.JOB_TTL_HOURS + 1)
        db.commit()
    row = jobs(client, club)[0]
    assert row["status"] == "cancelled" and all(t["message"] == "Истёк срок задания" for t in row["targets"])
    assert start(client, club, source).status_code == 200                                 # ПК освободились


# ---------- список игр на ПК ----------
def games_body(entries):
    return {"root": "D:\\Games", "entries": entries}


def test_the_pc_reports_its_games_and_the_admin_sees_them(env):
    client, club, source, one, two = env
    good = {"path": "steamapps/common/Dota 2", "name": "Dota 2", "kind": "steam", "size_bytes": 40_000_000_000, "files": 1200}
    junk = [{"path": "", "name": "x", "size_bytes": 1, "files": 1}, {"path": "a", "name": "b", "kind": "weird", "size_bytes": 1, "files": 1}, "строка", {"path": "ok"}]
    r = client.post("/api/agent/games", json=games_body([good, *junk, {"path": "Tools", "name": "Tools", "size_bytes": 5, "files": 2}]), headers=source["headers"])
    assert r.status_code == 200 and r.json()["count"] == 2                                # негодные записи отброшены, остальные приняты
    view = client.get(f"/api/admin/pcs/{source['id']}/games", headers=club["staff"]).json()
    assert view["root"] == "D:\\Games" and view["pending"] is False and view["pc"]["name"] == "SRC"
    assert [e["name"] for e in view["entries"]] == ["Dota 2", "Tools"]
    assert view["entries"][0]["size_bytes"] == 40_000_000_000 and view["entries"][1]["kind"] == "dir"
    assert client.post("/api/agent/games", json={"root": "x", "entries": "не список"}, headers=source["headers"]).status_code == 422


def test_asking_for_a_rescan_reaches_the_pc_until_it_answers(env):
    client, club, source, one, two = env
    assert "scan_games" not in raw_state(client, source)
    r = client.post(f"/api/admin/pcs/{source['id']}/games/rescan", headers=club["staff"])
    assert r.status_code == 200 and r.json()["pending"] is True
    assert raw_state(client, source)["scan_games"] is True
    client.post("/api/agent/games", json=games_body([]), headers=source["headers"])
    assert "scan_games" not in raw_state(client, source)
    other = make_club(client, slug="club-b", phone=PHONE_B)
    assert client.get(f"/api/admin/pcs/{source['id']}/games", headers=other["staff"]).status_code == 404
    assert client.post(f"/api/admin/pcs/{source['id']}/games/rescan", headers=other["staff"]).status_code == 404
