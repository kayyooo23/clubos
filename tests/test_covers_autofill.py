"""Постеры Steam подтягиваются сами, без кнопки: когда сотрудник добавил игру и когда ПК запросил меню, а у игры из Steam нет обложки.
Скачивание подменено, в настоящий Steam тесты не ходят."""
import pytest

from app import config
from app.services import covers

from .helpers import make_club, make_pc
from .test_ads import png
from .test_covers import CHROME, CS2, DOTA, add, admin_app, upload

POSTER = png(600, 900)


class Steam:
    """Подменённое скачивание: помнит, какие игры спрашивали, и отдаёт постер только для известных номеров."""

    def __init__(self, known=("570", "730")):
        self.known, self.asked = set(known), []

    def __call__(self, steam_id):
        self.asked.append(steam_id)
        return POSTER if steam_id in self.known else None


@pytest.fixture()
def env(client, monkeypatch):
    monkeypatch.setattr(covers, "_autofill_last", {})
    monkeypatch.setattr(covers, "_autofill_running", set())
    club = make_club(client)
    pc = make_pc(client, club)
    return client, club, pc


@pytest.fixture()
def steam(monkeypatch):
    fake = Steam()
    monkeypatch.setattr(covers, "fetch_steam_cover", fake)
    return fake


def games_on(monkeypatch, on):
    monkeypatch.setattr(config, "STEAM_AUTOCOVERS", on)


def test_adding_a_steam_game_fetches_its_poster_by_itself(env, steam, monkeypatch):
    client, club, _ = env
    games_on(monkeypatch, True)
    dota = add(client, club, DOTA)
    assert admin_app(client, club, dota["id"])["cover_source"] == "steam"            # кнопку нажимать не пришлось
    assert steam.asked == ["570"]
    add(client, club, CHROME)
    assert steam.asked == ["570"]                                                    # у программы номера Steam нет: ничего не спрашиваем


def test_the_pc_asking_for_the_menu_fetches_posters_for_games_added_earlier(env, steam, monkeypatch):
    client, club, pc = env
    games_on(monkeypatch, False)                                                     # игры уже лежат в каталоге (например, стартовый набор), обложек нет
    dota, cs2 = add(client, club, DOTA), add(client, club, CS2)
    assert steam.asked == []
    games_on(monkeypatch, True)
    first = client.get("/api/agent/apps", headers=pc["headers"]).json()
    assert [a["cover_version"] for a in first] == [None, None]                       # ответ уходит сразу, не дожидаясь Steam
    assert sorted(steam.asked) == ["570", "730"]
    assert admin_app(client, club, dota["id"])["cover_source"] == "steam" and admin_app(client, club, cs2["id"])["cover_source"] == "steam"
    again = client.get("/api/agent/apps", headers=pc["headers"]).json()
    assert all(a["cover_version"] for a in again)                                    # в следующий раз меню уже получает обложки


def test_posters_are_not_requested_more_often_than_every_ten_minutes(env, monkeypatch):
    client, club, pc = env
    games_on(monkeypatch, False)
    add(client, club, DOTA)
    games_on(monkeypatch, True)
    nothing = Steam(known=())                                                        # Steam постера не нашёл
    monkeypatch.setattr(covers, "fetch_steam_cover", nothing)
    clock = {"t": 1000.0}
    monkeypatch.setattr(covers.time, "monotonic", lambda: clock["t"])
    client.get("/api/agent/apps", headers=pc["headers"])
    assert nothing.asked == ["570"]
    client.get("/api/agent/apps", headers=pc["headers"])
    clock["t"] += 599
    client.get("/api/agent/apps", headers=pc["headers"])
    assert nothing.asked == ["570"]                                                  # меню спрашивают каждые несколько секунд, а Steam не дёргаем
    clock["t"] += 2
    client.get("/api/agent/apps", headers=pc["headers"])
    assert nothing.asked == ["570", "570"]                                           # прошло десять минут: пробуем снова


def test_a_manual_cover_is_never_replaced_by_the_automatic_one(env, steam, monkeypatch):
    client, club, _ = env
    games_on(monkeypatch, False)
    cs2 = add(client, club, CS2)
    mine = upload(client, club, cs2["id"], png(600, 900, extra=b"mine")).json()["cover_version"]
    games_on(monkeypatch, True)
    add(client, club, DOTA)
    assert steam.asked == ["570"]
    assert admin_app(client, club, cs2["id"])["cover_version"] == mine and admin_app(client, club, cs2["id"])["cover_source"] == "manual"


def test_nothing_is_fetched_when_the_option_is_off(env, steam, monkeypatch):
    client, club, pc = env
    games_on(monkeypatch, False)
    add(client, club, DOTA)
    client.get("/api/agent/apps", headers=pc["headers"])
    assert steam.asked == []


def test_a_menu_where_every_steam_game_has_a_poster_starts_no_background_work(env, steam, monkeypatch):
    client, club, pc = env
    games_on(monkeypatch, True)
    add(client, club, DOTA)
    assert steam.asked == ["570"]
    client.get("/api/agent/apps", headers=pc["headers"])
    assert steam.asked == ["570"]


def test_a_failure_in_the_background_does_not_break_the_request_and_frees_the_slot(env, monkeypatch):
    client, club, pc = env

    def boom(steam_id):
        raise RuntimeError("Steam лёг")

    monkeypatch.setattr(covers, "fetch_steam_cover", boom)
    games_on(monkeypatch, True)
    r = client.post("/api/admin/apps", json=DOTA, headers=club["staff"])
    assert r.status_code == 200 and client.get("/api/agent/apps", headers=pc["headers"]).status_code == 200
    assert covers._autofill_running == set()                                         # следующая попытка не заблокирована


def test_only_one_download_per_club_runs_at_a_time(env, steam):
    client, club, _ = env
    add(client, club, DOTA)
    covers._autofill_running.add(club["id"])
    assert covers.autofill_steam(club["id"], steam, force=True) is None
    covers._autofill_running.clear()
    assert covers.autofill_steam(club["id"], steam, force=True) == {"added": 1, "not_found": 0, "left": 0}


def test_one_clubs_posters_do_not_touch_another_club(env, steam, monkeypatch):
    client, club, _ = env
    games_on(monkeypatch, False)
    add(client, club, DOTA)
    other = make_club(client, slug="club-b", phone="+79161234567")
    games_on(monkeypatch, True)
    add(client, other, CS2)                                                          # игра второго клуба: постеры подтянутся только ему
    assert steam.asked == ["730"]
    assert admin_app(client, club, 1)["cover_version"] is None
