"""Каталог игр для меню гостя: управление из админки, выдача клиенту на ПК, изоляция клубов, проверка путей запуска."""
import pytest

from .helpers import PHONE_B, PLATFORM, make_club, make_guest, make_pc

DOTA = {"title": "Dota 2", "tab": "games", "category": "MOBA", "age_rating": 12,
        "launch_kind": "steam", "launch_target": "570"}
CHROME = {"title": "Chrome", "tab": "apps", "category": "Браузеры",
          "launch_kind": "exe", "launch_target": r"C:\Program Files\Google\Chrome\Application\chrome.exe"}


@pytest.fixture()
def env(client):
    club = make_club(client)
    pc = make_pc(client, club)
    return client, club, pc


def add(client, club, body):
    r = client.post("/api/admin/apps", json=body, headers=club["staff"])
    assert r.status_code == 200, r.text
    return r.json()


def test_staff_manages_the_catalog_and_pc_sees_only_active_entries(env):
    client, club, pc = env
    dota = add(client, club, DOTA)
    chrome = add(client, club, CHROME)

    # клиент на ПК видит оба, без служебных полей
    seen = client.get("/api/agent/apps", headers=pc["headers"]).json()
    assert [a["title"] for a in seen] == ["Dota 2", "Chrome"]
    assert set(seen[0]) == {"id", "title", "tab", "category", "age_rating", "launch_kind", "launch_target", "cover_version"}

    # скрытая запись пропадает у клиента, но остаётся в админке
    r = client.patch(f"/api/admin/apps/{dota['id']}", json={"active": False}, headers=club["staff"])
    assert r.status_code == 200 and r.json()["active"] is False
    assert [a["title"] for a in client.get("/api/agent/apps", headers=pc["headers"]).json()] == ["Chrome"]
    assert len(client.get("/api/admin/apps", headers=club["staff"]).json()) == 2

    # удаление убирает совсем
    assert client.delete(f"/api/admin/apps/{chrome['id']}", headers=club["staff"]).status_code == 200
    assert client.get("/api/agent/apps", headers=pc["headers"]).json() == []


def test_order_follows_sort_order_then_creation(env):
    client, club, pc = env
    add(client, club, {**DOTA, "title": "Третья", "sort_order": 300})
    add(client, club, {**DOTA, "title": "Первая", "sort_order": 10})
    add(client, club, {**DOTA, "title": "Вторая", "sort_order": 10})
    titles = [a["title"] for a in client.get("/api/agent/apps", headers=pc["headers"]).json()]
    assert titles == ["Первая", "Вторая", "Третья"]


def test_patch_changes_only_what_was_sent_and_revalidates_the_whole_entry(env):
    client, club, pc = env
    dota = add(client, club, DOTA)
    r = client.patch(f"/api/admin/apps/{dota['id']}", json={"category": "Шутеры", "age_rating": None},
                     headers=club["staff"])
    assert r.status_code == 200
    assert (r.json()["category"], r.json()["age_rating"], r.json()["title"]) == ("Шутеры", None, "Dota 2")

    # сменили способ запуска, а цель осталась числом: запись целиком больше не валидна
    r = client.patch(f"/api/admin/apps/{dota['id']}", json={"launch_kind": "exe"}, headers=club["staff"])
    assert r.status_code == 422 and "exe" in r.json()["detail"]
    # и в базе осталась прежняя
    assert client.get("/api/admin/apps", headers=club["staff"]).json()[0]["launch_kind"] == "steam"

    # неизвестные поля не принимаем (нельзя подсунуть club_id)
    assert client.patch(f"/api/admin/apps/{dota['id']}", json={"club_id": 99}, headers=club["staff"]).status_code == 422


@pytest.mark.parametrize("kind,target", [
    ("steam", "dota"), ("steam", "0570"), ("steam", "-1"), ("steam", "570 && calc"), ("steam", "12345678901"),
    ("exe", "game.exe"), ("exe", r"\\evil\share\game.exe"), ("exe", r"C:\Games\..\Windows\cmd.exe"),
    ("exe", r"C:\Games\game.bat"), ("exe", r"C:\Games\game.exe /c calc"), ("exe", r"C:\Games\\game.exe"),
    ("exe", "C:/Games/game.exe"), ("exe", r"C:\Games\ga|me.exe"), ("exe", "ftp://host/game.exe"),
])
def test_dangerous_or_malformed_launch_targets_are_rejected(env, kind, target):
    client, club, pc = env
    r = client.post("/api/admin/apps", json={**DOTA, "launch_kind": kind, "launch_target": target},
                    headers=club["staff"])
    assert r.status_code == 422, (kind, target, r.text)


def test_valid_paths_with_spaces_and_brackets_are_accepted(env):
    client, club, pc = env
    add(client, club, {**CHROME, "launch_target": r"D:\Games (x86)\My Game\Bin\game.EXE"})


def test_text_fields_are_trimmed_and_blank_ones_refused(env):
    client, club, pc = env
    a = add(client, club, {**DOTA, "title": "  Dota 2  ", "category": " MOBA "})
    assert (a["title"], a["category"]) == ("Dota 2", "MOBA")
    assert client.post("/api/admin/apps", json={**DOTA, "title": "   "}, headers=club["staff"]).status_code == 422
    assert client.post("/api/admin/apps", json={**DOTA, "tab": "movies"}, headers=club["staff"]).status_code == 422
    assert client.post("/api/admin/apps", json={**DOTA, "age_rating": 7}, headers=club["staff"]).status_code == 422


def test_catalog_is_isolated_between_clubs(client):
    a = make_club(client, "club-a")
    b = make_club(client, "club-b", phone=PHONE_B)
    pc_a, pc_b = make_pc(client, a), make_pc(client, b)
    entry = add(client, a, DOTA)

    assert client.get("/api/admin/apps", headers=b["staff"]).json() == []
    assert client.get("/api/agent/apps", headers=pc_b["headers"]).json() == []
    # чужую запись нельзя ни править, ни удалить
    assert client.patch(f"/api/admin/apps/{entry['id']}", json={"title": "X"}, headers=b["staff"]).status_code == 404
    assert client.delete(f"/api/admin/apps/{entry['id']}", headers=b["staff"]).status_code == 404
    assert client.get("/api/admin/apps", headers=a["staff"]).json()[0]["title"] == "Dota 2"


def test_catalog_needs_the_right_credentials(env):
    client, club, pc = env
    assert client.get("/api/admin/apps").status_code in (401, 403)
    assert client.post("/api/admin/apps", json=DOTA).status_code in (401, 403)
    assert client.get("/api/agent/apps").status_code in (401, 403)
    # ключ ПК не открывает админский каталог, токен гостя не нужен для чтения каталога, но токен ПК-входа не пускает в админку
    guest = make_guest(client, club, pc)
    assert client.get("/api/admin/apps", headers=guest["headers"]).status_code in (401, 403)
    assert client.post("/api/admin/apps", json=DOTA, headers=guest["headers"]).status_code in (401, 403)
    assert client.get("/api/agent/apps", headers=guest["headers"]).status_code == 200


def test_changes_are_audited_without_personal_data(env):
    client, club, pc = env
    a = add(client, club, DOTA)
    client.patch(f"/api/admin/apps/{a['id']}", json={"active": False}, headers=club["staff"])
    client.delete(f"/api/admin/apps/{a['id']}", headers=club["staff"])
    log = client.get("/api/admin/audit", headers=club["staff"]).json()
    actions = [row["action"] for row in log if row["action"].startswith("app_")]
    assert actions == ["app_delete", "app_update", "app_create"]


def test_catalog_size_is_capped(env):
    client, club, pc = env
    from app.db import SessionLocal
    from app.models import CatalogApp
    from app.services.catalog import MAX_APPS_PER_CLUB
    with SessionLocal() as db:
        db.add_all(CatalogApp(club_id=club["id"], title=f"G{i}", launch_kind="steam", launch_target="570")
                   for i in range(MAX_APPS_PER_CLUB))
        db.commit()
    r = client.post("/api/admin/apps", json=DOTA, headers=club["staff"])
    assert r.status_code == 409


def test_platform_copies_the_catalog_to_another_club(client):
    a = make_club(client, "club-a")
    b = make_club(client, "club-b", phone=PHONE_B)
    add(client, a, DOTA)
    hidden = add(client, a, {**CHROME, "active": False})
    add(client, b, {**DOTA, "title": "Своя игра"})

    # без replace копируем сверху, скрытые записи тоже
    r = client.post(f"/api/platform/clubs/{b['id']}/copy-apps", headers=PLATFORM, json={"from_club_id": a["id"]})
    assert r.status_code == 200 and r.json() == {"copied": 2}
    titles = sorted(x["title"] for x in client.get("/api/admin/apps", headers=b["staff"]).json())
    assert titles == ["Chrome", "Dota 2", "Своя игра"]

    # с replace список получателя заменяется целиком; копии независимы от исходника
    r = client.post(f"/api/platform/clubs/{b['id']}/copy-apps", headers=PLATFORM,
                    json={"from_club_id": a["id"], "replace": True})
    assert r.json() == {"copied": 2}
    copied = client.get("/api/admin/apps", headers=b["staff"]).json()
    assert sorted(x["title"] for x in copied) == ["Chrome", "Dota 2"]
    assert next(x for x in copied if x["title"] == "Chrome")["active"] is False
    client.delete(f"/api/admin/apps/{copied[0]['id']}", headers=b["staff"])
    assert len(client.get("/api/admin/apps", headers=a["staff"]).json()) == 2


def test_platform_copy_guards(client):
    a = make_club(client, "club-a")
    assert client.post(f"/api/platform/clubs/{a['id']}/copy-apps", headers=PLATFORM,
                       json={"from_club_id": a["id"]}).status_code == 422
    assert client.post(f"/api/platform/clubs/{a['id']}/copy-apps", headers=PLATFORM,
                       json={"from_club_id": 999}).status_code == 404
    assert client.post(f"/api/platform/clubs/{a['id']}/copy-apps",
                       json={"from_club_id": a["id"]}).status_code in (401, 403)


def test_starter_catalog_entries_pass_the_same_validation_and_seed_only_into_an_empty_catalog(env):
    from app.db import SessionLocal
    from app.schemas import AppIn
    from app.services import catalog
    from app.timeutil import utcnow
    client, club, pc = env
    with SessionLocal() as db:
        assert catalog.seed_starter_catalog(db, club["id"], utcnow()) > 0
        db.commit()
        assert catalog.seed_starter_catalog(db, club["id"], utcnow()) == 0  # повторно и в непустой каталог не пишет
        db.commit()
    seen = client.get("/api/admin/apps", headers=club["staff"]).json()
    assert len(seen) == len(catalog.STARTER_APPS) + 2
    for a in seen:  # всё, что мы кладём сами, проходит ту же проверку, что и ввод из админки
        AppIn.model_validate({k: a[k] for k in ("title", "tab", "category", "age_rating", "launch_kind", "launch_target",
                                                "sort_order", "active")})
    assert {a["tab"] for a in seen} == {"games", "apps"}


def test_a_folder_path_gets_a_clear_hint_to_use_the_exe_file(env):
    client, club, pc = env
    r = client.post("/api/admin/apps", json={**DOTA, "launch_kind": "exe", "launch_target": r"C:\Users\katya\AppData\Local\Programs\TeamSpeak 3 Client"},
                    headers=club["staff"])
    assert r.status_code == 422
    assert "оканчивается на .exe" in r.text and "не к папке" in r.text
