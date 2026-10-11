"""Список программ на ПК: служба присылает найденные ярлыки, администратор выбирает из них программу для меню (вместо ручного ввода пути)."""

import pytest



import base64



from .helpers import PHONE_B, make_club, make_pc, raw_state

from .test_ads import png



TS = {"name": "TeamSpeak 3", "path": r"C:\Program Files\TeamSpeak 3 Client\ts3client_win64.exe"}

CHROME = {"name": "Google Chrome", "path": r"C:\Program Files\Google\Chrome\Application\chrome.exe"}





@pytest.fixture()

def env(client):

    club = make_club(client)

    pc = make_pc(client, club)

    return client, club, pc





def report(client, pc, programs):

    return client.post("/api/agent/programs", json={"programs": programs}, headers=pc["headers"])





def view(client, club, pc):

    r = client.get(f"/api/admin/pcs/{pc['id']}/programs", headers=club["staff"])

    assert r.status_code == 200, r.text

    return r.json()





def test_before_any_report_the_list_is_empty(env):

    client, club, pc = env

    data = view(client, club, pc)

    assert data["programs"] == [] and data["reported_at"] is None and data["pending"] is False

    assert data["pc"]["name"] == "PC-1"





def test_the_list_sent_by_the_pc_is_what_the_admin_sees(env):

    client, club, pc = env

    r = report(client, pc, [TS, CHROME])

    assert r.status_code == 200 and r.json() == {"ok": True, "accepted": 2}

    data = view(client, club, pc)

    assert data["programs"] == [TS, CHROME] and data["reported_at"] is not None





def test_a_new_report_replaces_the_old_list(env):

    client, club, pc = env

    report(client, pc, [TS, CHROME])

    report(client, pc, [CHROME])

    assert view(client, club, pc)["programs"] == [CHROME]





@pytest.mark.parametrize("bad", [

    {"name": "Скрипт", "path": r"C:\Games\run.bat"},                      # не exe

    {"name": "Сеть", "path": r"\\server\share\app.exe"},                  # сетевой путь

    {"name": "Выход", "path": r"C:\Games\..\Windows\cmd.exe"},            # выход из папки

    {"name": "Относительный", "path": "app.exe"},

    {"name": "Слэши", "path": "C:/Games/app.exe"},

    {"name": "", "path": r"C:\Games\app.exe"},                            # без названия

    {"name": "Длинный", "path": "C:\\" + "a" * 300 + ".exe"},

    {"name": 5, "path": r"C:\Games\app.exe"},

    {"path": r"C:\Games\app.exe"},

    "строка",

])

def test_unsuitable_entries_are_dropped_but_good_ones_are_kept(env, bad):

    client, club, pc = env

    r = report(client, pc, [bad, TS])

    assert r.status_code == 200 and r.json()["accepted"] == 1

    assert view(client, club, pc)["programs"] == [TS]





def test_duplicates_are_merged_and_the_name_is_one_line(env):

    client, club, pc = env

    r = report(client, pc, [{"name": "Chrome\n  Browser", "path": CHROME["path"]}, {"name": "Другое имя", "path": CHROME["path"].upper()}])

    assert r.json()["accepted"] == 1

    assert view(client, club, pc)["programs"][0]["name"] == "Chrome Browser"





def test_the_list_is_capped(env):

    client, club, pc = env

    many = [{"name": f"App {i}", "path": rf"C:\Apps\app{i}.exe"} for i in range(900)]

    assert report(client, pc, many).json()["accepted"] == 800





def test_a_wrong_shape_is_a_clear_error(env):

    client, club, pc = env

    assert client.post("/api/agent/programs", json={"programs": "нет"}, headers=pc["headers"]).status_code == 422

    assert client.post("/api/agent/programs", json={"programs": []}).status_code == 401           # без ключа ПК





def test_a_rescan_request_reaches_the_pc_in_the_state_and_clears_after_the_report(env):

    client, club, pc = env

    assert "scan_programs" not in raw_state(client, pc)                  # пока никто не просил, ответ прежний

    r = client.post(f"/api/admin/pcs/{pc['id']}/programs/rescan", headers=club["staff"])

    assert r.status_code == 200 and r.json()["pending"] is True

    assert raw_state(client, pc)["scan_programs"] is True

    report(client, pc, [TS])

    assert "scan_programs" not in raw_state(client, pc)

    assert view(client, club, pc)["pending"] is False





def test_an_old_report_does_not_clear_a_newer_request(env):

    client, club, pc = env

    report(client, pc, [TS])

    client.post(f"/api/admin/pcs/{pc['id']}/programs/rescan", headers=club["staff"])

    assert raw_state(client, pc)["scan_programs"] is True               # отчёт был раньше просьбы: просьба в силе





def test_online_flag_follows_the_heartbeat(env):

    client, club, pc = env

    assert view(client, club, pc)["pc"]["online"] is False

    raw_state(client, pc)                                                # ПК вышел на связь

    assert view(client, club, pc)["pc"]["online"] is True





def test_another_club_cannot_read_or_request_the_list(env):

    client, club, pc = env

    other = make_club(client, slug="club-b", phone=PHONE_B)

    assert client.get(f"/api/admin/pcs/{pc['id']}/programs", headers=other["staff"]).status_code == 404

    assert client.post(f"/api/admin/pcs/{pc['id']}/programs/rescan", headers=other["staff"]).status_code == 404

    assert client.get(f"/api/admin/pcs/{pc['id']}/programs").status_code == 401

    assert "scan_programs" not in raw_state(client, pc)





def test_pcs_do_not_see_each_others_lists(env):

    client, club, pc = env

    pc2 = make_pc(client, club, "PC-2")

    report(client, pc, [TS])

    assert view(client, club, pc2)["programs"] == []


# ---------- иконки программ ----------
def icon(width=96, height=96):
    return base64.b64encode(png(width, height)).decode()


def test_a_program_icon_is_stored_and_returned_to_the_admin(env):
    client, club, pc = env
    report(client, pc, [{**TS, "icon": icon()}, CHROME])
    programs = {p["name"]: p for p in view(client, club, pc)["programs"]}
    assert programs["TeamSpeak 3"]["icon"] == icon()
    assert "icon" not in programs["Google Chrome"]


def test_the_light_view_for_polling_has_no_icon_bytes_only_a_flag(env):
    client, club, pc = env
    report(client, pc, [{**TS, "icon": icon()}, CHROME])
    data = client.get(f"/api/admin/pcs/{pc['id']}/programs", params={"icons": "false"}, headers=club["staff"]).json()
    by_name = {p["name"]: p for p in data["programs"]}
    assert by_name["TeamSpeak 3"] == {"name": "TeamSpeak 3", "path": TS["path"], "has_icon": True}
    assert by_name["Google Chrome"]["has_icon"] is False


@pytest.mark.parametrize("bad", [
    "это не base64!",
    base64.b64encode(b"GIF89a" + bytes(50)).decode(),                     # не PNG
    base64.b64encode(b"\x89PNG\r\n\x1a\n" + b"junk").decode(),             # обрезанный заголовок
    "A" * 50_000,                                                          # слишком большая
    "",
    123,
], ids=["not-base64", "gif", "truncated", "too-big", "empty", "number"])
def test_an_unsuitable_icon_is_dropped_but_the_program_stays(env, bad):
    client, club, pc = env
    report(client, pc, [{**TS, "icon": bad}])
    (program,) = view(client, club, pc)["programs"]
    assert program["path"] == TS["path"] and "icon" not in program


def test_an_icon_of_a_strange_size_is_dropped(env):
    client, club, pc = env
    report(client, pc, [{**TS, "icon": icon(8, 8)}, {**CHROME, "icon": icon(512, 512)}])
    assert all("icon" not in p for p in view(client, club, pc)["programs"])


def test_the_total_size_of_icons_is_capped(env):
    client, club, pc = env
    big = base64.b64encode(png(256, 256, extra=bytes(4) + b"tEXt" + b"x" * 0x4000)).decode()
    assert len(big) < 40_000                                                # одна иконка проходит, а на все места не хватит
    many = [{"name": f"App {i}", "path": f"C:\\Apps\\app{i}.exe", "icon": big} for i in range(300)]
    r = report(client, pc, many)
    assert r.status_code == 200 and r.json()["accepted"] == 300            # программы приняты все
    with_icon = [p for p in view(client, club, pc)["programs"] if "icon" in p]
    assert 0 < len(with_icon) < 300 and sum(len(p["icon"]) for p in with_icon) <= 4_000_000


# ---------- иконка ставится записи меню сама ----------
def add_entry(client, club, path, tab="apps", kind="exe", title="TeamSpeak 3"):
    r = client.post("/api/admin/apps", headers=club["staff"], json={
        "title": title, "tab": tab, "category": "Общение", "launch_kind": kind, "launch_target": path})
    assert r.status_code == 200, r.text
    return r.json()


def entry(client, club, app_id):
    return next(a for a in client.get("/api/admin/apps", headers=club["staff"]).json() if a["id"] == app_id)


def test_a_new_program_entry_gets_the_icon_the_pc_reported_for_its_path(env):
    client, club, pc = env
    report(client, pc, [{**TS, "icon": icon()}])
    added = add_entry(client, club, TS["path"])
    assert added["cover_source"] == "program" and added["cover_version"]
    cover = client.get(f"/api/admin/apps/{added['id']}/cover", headers=club["staff"])
    assert cover.status_code == 200 and cover.headers["content-type"] == "image/png"
    assert cover.content == base64.b64decode(icon())


def test_the_path_is_compared_without_regard_to_letter_case(env):
    client, club, pc = env
    report(client, pc, [{**TS, "icon": icon()}])
    added = add_entry(client, club, TS["path"].upper())
    assert added["cover_source"] == "program"


def test_an_entry_added_earlier_gets_the_icon_when_the_pc_reports_later(env):
    client, club, pc = env
    added = add_entry(client, club, TS["path"])
    assert added["cover_version"] is None
    report(client, pc, [{**TS, "icon": icon()}, CHROME])
    assert entry(client, club, added["id"])["cover_source"] == "program"


def test_a_manual_cover_is_never_replaced_by_a_program_icon(env):
    client, club, pc = env
    added = add_entry(client, club, TS["path"])
    manual = png(200, 300)
    r = client.put(f"/api/admin/apps/{added['id']}/cover", content=manual, headers={**club["staff"], "Content-Type": "image/png"})
    assert r.status_code == 200
    report(client, pc, [{**TS, "icon": icon()}])
    now = entry(client, club, added["id"])
    assert now["cover_source"] == "manual"
    assert client.get(f"/api/admin/apps/{added['id']}/cover", headers=club["staff"]).content == manual


def test_games_and_steam_entries_do_not_take_program_icons(env):
    client, club, pc = env
    report(client, pc, [{**TS, "icon": icon()}])
    game = add_entry(client, club, TS["path"], tab="games", title="Игра")
    steam = add_entry(client, club, "570", kind="steam", title="Dota")
    assert game["cover_version"] is None and steam["cover_version"] is None


def test_changing_the_path_drops_the_old_program_icon_and_takes_the_new_one(env):
    client, club, pc = env
    other = base64.b64encode(png(64, 64)).decode()
    report(client, pc, [{**TS, "icon": icon()}, {**CHROME, "icon": other}])
    added = add_entry(client, club, TS["path"])
    first = added["cover_version"]
    r = client.patch(f"/api/admin/apps/{added['id']}", json={"launch_target": CHROME["path"]}, headers=club["staff"])
    assert r.status_code == 200
    assert r.json()["cover_source"] == "program" and r.json()["cover_version"] != first       # иконка Chrome, а не TeamSpeak
    # путь, которого нет среди найденных программ: чужая иконка не остаётся
    r = client.patch(f"/api/admin/apps/{added['id']}", json={"launch_target": r"C:\Nothing\here.exe"}, headers=club["staff"])
    assert r.json()["cover_version"] is None
    # правка, не трогающая путь, иконку не сбрасывает
    r = client.patch(f"/api/admin/apps/{added['id']}", json={"launch_target": CHROME["path"]}, headers=club["staff"])
    keep = r.json()["cover_version"]
    r = client.patch(f"/api/admin/apps/{added['id']}", json={"title": "Другое имя"}, headers=club["staff"])
    assert r.json()["cover_version"] == keep


def test_another_clubs_icons_are_not_used(client):
    a = make_club(client)
    pc_a = make_pc(client, a)
    b = make_club(client, slug="club-b", phone=PHONE_B)
    report(client, pc_a, [{**TS, "icon": icon()}])
    added = add_entry(client, b, TS["path"])
    assert added["cover_version"] is None
