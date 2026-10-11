"""Реклама партнёров на экране блокировки: баннеры-картинки, включение у клуба, ротация, учёт времени показа, отчёт."""
import struct
import zlib
from datetime import date, datetime, timedelta, timezone

import pytest
from sqlalchemy import select

from app.db import SessionLocal
from app.models import AdStat, PC

from .helpers import PHONE_B, PLATFORM, make_club, make_guest, make_pc, make_tariff, start_for


def png(width=1200, height=400, extra=b"") -> bytes:
    """Настоящий PNG нужного размера (однотонный): заголовок, данные, конец."""
    def chunk(kind: bytes, body: bytes) -> bytes:
        return struct.pack(">I", len(body)) + kind + body + struct.pack(">I", zlib.crc32(kind + body) & 0xFFFFFFFF)
    raw = b"".join(b"\x00" + b"\x20\x20\x20" * width for _ in range(height))
    return b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 2, 0, 0, 0)) + chunk(b"IDAT", zlib.compress(raw)) + extra + chunk(b"IEND", b"")


def jpeg(width=1200, height=400) -> bytes:
    """Минимальный JPEG: достаточно, чтобы сервер прочитал тип и размер."""
    app0 = b"\xff\xe0" + struct.pack(">H", 16) + b"JFIF\x00\x01\x01\x00\x00\x01\x00\x01\x00\x00"
    sof = b"\xff\xc0" + struct.pack(">HBHHB", 11, 8, height, width, 1) + b"\x01\x11\x00"
    return b"\xff\xd8" + app0 + sof + b"\xff\xd9"


@pytest.fixture()
def env(client):
    club = make_club(client)
    pc = make_pc(client, club)
    return client, club, pc


def now_utc():
    return datetime.now(timezone.utc).replace(tzinfo=None)


def upload(client, ad_id, data, **headers):
    return client.put(f"/api/platform/ads/{ad_id}/image", content=data, headers={**PLATFORM, **headers})


def new_ad(client, name="Мама Рома, октябрь", image=True, **extra):
    r = client.post("/api/platform/ads", json={"text": name, **extra}, headers=PLATFORM)
    assert r.status_code == 200, r.text
    ad = r.json()
    if image:
        got = upload(client, ad["id"], png())
        assert got.status_code == 200, got.text
        ad = got.json()
    return ad


def enable(client, club, enabled=True, share=None):
    body = {"enabled": enabled, **({"club_share": share} if share is not None else {})}
    r = client.post(f"/api/platform/clubs/{club['id']}/ads", json=body, headers=PLATFORM)
    assert r.status_code == 200, r.text
    return r.json()


def screen(client, pc):
    return client.get("/api/agent/ads", headers=pc["headers"]).json()


def age_mark(pc_id, seconds):
    """«Прошло время» с последнего учёта показа на ПК."""
    with SessionLocal() as db:
        row = db.get(PC, pc_id)
        row.ads_seen_at -= timedelta(seconds=seconds)
        db.commit()


def stats():
    with SessionLocal() as db:
        return [(s.club_id, s.ad_id, s.seconds) for s in db.scalars(select(AdStat).order_by(AdStat.id))]


# ---------- баннеры: заводит платформа ----------
def test_platform_manages_banners_and_the_name_is_cleaned_to_one_line(client):
    a = new_ad(client, "  Кофе\n\t в «Бодрой точке»   -20%  ", advertiser="ООО «Бодрая точка», ИНН 7700000000", erid="2VtzqwP12ab")
    assert a["text"] == "Кофе в «Бодрой точке» -20%" and a["active"] is True and a["duration_seconds"] == 10
    assert (a["has_image"], a["width"], a["height"]) == (True, 1200, 400) and a["version"]
    assert client.patch(f"/api/platform/ads/{a['id']}", json={"active": False, "sort_order": 5, "duration_seconds": 15}, headers=PLATFORM).json()["active"] is False
    (listed,) = client.get("/api/platform/ads", headers=PLATFORM).json()
    assert listed["erid"] == "2VtzqwP12ab" and listed["sort_order"] == 5 and listed["duration_seconds"] == 15 and listed["has_image"]
    assert client.delete(f"/api/platform/ads/{a['id']}", headers=PLATFORM).status_code == 200
    assert client.get("/api/platform/ads", headers=PLATFORM).json() == []
    with SessionLocal() as db:
        from app.models import AdImage
        assert db.scalar(select(AdImage)) is None               # картинка удалена вместе с баннером


@pytest.mark.parametrize("body", [{"text": ""}, {"text": "   \n  "}, {"text": "x" * 201}, {"text": "ok", "erid": "e" * 81},
                                  {"text": "ok", "starts_at": "2026-12-02T00:00:00", "ends_at": "2026-12-01T00:00:00"},
                                  {"text": "ok", "sort_order": -1}, {"text": "ok", "duration_seconds": 2}, {"text": "ok", "duration_seconds": 121}])
def test_bad_banners_are_refused(client, body):
    assert client.post("/api/platform/ads", json=body, headers=PLATFORM).status_code == 422


def test_patch_revalidates_the_whole_banner_and_refuses_unknown_fields(client):
    a = new_ad(client)
    assert client.patch(f"/api/platform/ads/{a['id']}", json={"text": ""}, headers=PLATFORM).status_code == 422
    assert client.patch(f"/api/platform/ads/{a['id']}", json={"duration_seconds": 1}, headers=PLATFORM).status_code == 422
    assert client.patch(f"/api/platform/ads/{a['id']}", json={"ends_at": "2000-01-01T00:00:00", "starts_at": "2001-01-01T00:00:00"},
                        headers=PLATFORM).status_code == 422
    assert client.patch(f"/api/platform/ads/{a['id']}", json={"club_id": 1}, headers=PLATFORM).status_code == 422
    assert client.patch("/api/platform/ads/999", json={"active": False}, headers=PLATFORM).status_code == 404


def test_ads_are_platform_only(env):
    client, club, pc = env
    ad = new_ad(client)
    assert client.get("/api/platform/ads").status_code in (401, 403)
    assert client.post("/api/platform/ads", json={"text": "x"}, headers=club["staff"]).status_code in (401, 403)
    assert client.get("/api/platform/ads", headers=pc["headers"]).status_code in (401, 403)
    assert client.get("/api/platform/ads/report", headers=club["staff"]).status_code in (401, 403)
    assert client.put(f"/api/platform/ads/{ad['id']}/image", content=png()).status_code in (401, 403)
    assert client.put(f"/api/platform/ads/{ad['id']}/image", content=png(), headers=club["staff"]).status_code in (401, 403)
    assert client.get(f"/api/platform/ads/{ad['id']}/image", headers=club["staff"]).status_code in (401, 403)


# ---------- картинки ----------
def test_png_and_jpeg_are_accepted_and_the_picture_can_be_replaced(client):
    ad = new_ad(client, image=False)
    assert ad["has_image"] is False
    first = upload(client, ad["id"], png(1200, 400)).json()
    assert (first["width"], first["height"]) == (1200, 400)
    second = upload(client, ad["id"], jpeg(970, 250)).json()
    assert (second["width"], second["height"]) == (970, 250) and second["version"] != first["version"]
    got = client.get(f"/api/platform/ads/{ad['id']}/image", headers=PLATFORM)
    assert got.status_code == 200 and got.headers["content-type"] == "image/jpeg" and got.content == jpeg(970, 250)
    assert client.get("/api/platform/ads/999/image", headers=PLATFORM).status_code == 404
    assert client.get(f"/api/platform/ads/{new_ad(client, image=False)['id']}/image", headers=PLATFORM).status_code == 404


def test_the_same_picture_gets_the_same_version(client):
    a, b = new_ad(client), new_ad(client)
    assert a["version"] == b["version"]


@pytest.mark.parametrize("data,code", [
    (b"", 422),                                                    # пустой файл
    (b"GIF89a" + b"\x00" * 100, 415),                              # GIF
    (b"RIFF\x00\x00\x00\x00WEBPVP8 " + b"\x00" * 50, 415),         # WebP
    (b"<svg xmlns='http://www.w3.org/2000/svg'><script>alert(1)</script></svg>", 415),
    (b"MZ\x90\x00" + b"\x00" * 200, 415),                          # исполняемый файл с чужим именем
    (b"\x89PNG\r\n\x1a\n" + b"\x00" * 10, 422),                    # обрезанный PNG
    (b"\xff\xd8\xff\xe0\x00\x10JFIF", 422),                        # JPEG без размера
])
def test_wrong_files_are_refused_with_a_clear_reason(client, data, code):
    ad = new_ad(client, image=False)
    r = upload(client, ad["id"], data)
    assert r.status_code == code and isinstance(r.json()["detail"], str), r.text
    assert client.get("/api/platform/ads", headers=PLATFORM).json()[0]["has_image"] is False


@pytest.mark.parametrize("w,h", [(99, 400), (1200, 99), (4097, 400), (1200, 4097)])
def test_pictures_of_a_wrong_size_are_refused(client, w, h):
    ad = new_ad(client, image=False)
    r = upload(client, ad["id"], png(w, h) if max(w, h) <= 4097 and w * h < 4_000_000 else jpeg(w, h))
    assert r.status_code == 422 and "Размер" in r.json()["detail"]


def test_a_picture_over_two_megabytes_is_refused_even_if_it_is_a_valid_png(client):
    ad = new_ad(client, image=False)
    big = png(1200, 400, extra=b"\x00\x00\x40\x00tEXt" + b"x" * 0x4000)       # заведомо валидная картинка…
    big += b"\x00" * (2 * 1024 * 1024)                                          # …но тяжелее предела
    assert upload(client, ad["id"], big).status_code == 413


def test_uploading_to_a_missing_banner_is_a_404(client):
    assert upload(client, 999, png()).status_code == 404


# ---------- что показывается на ПК ----------
def test_ads_are_off_by_default_and_the_club_turns_them_on_itself(env):
    client, club, pc = env
    new_ad(client)
    assert screen(client, pc) == []                                          # клуб ещё не включил
    info = client.get("/api/admin/ads", headers=club["staff"]).json()
    assert info["enabled"] is False and info["club_share"] == 30 and info["active_count"] == 1
    assert client.post("/api/admin/ads/enabled", json={"enabled": True}, headers=club["staff"]).json()["enabled"] is True
    (seen,) = screen(client, pc)
    assert set(seen) == {"id", "advertiser", "erid", "seconds", "width", "height", "version"}   # название для владельца гостю не уходит
    assert client.post("/api/admin/ads/enabled", json={"enabled": False}, headers=club["staff"]).json()["enabled"] is False
    assert screen(client, pc) == []
    actions = [a["action"] for a in client.get("/api/admin/audit", headers=club["staff"]).json()]
    assert actions[:2] == ["ads_disabled", "ads_enabled"]


def test_a_banner_without_a_picture_is_never_shown(env):
    client, club, pc = env
    enable(client, club)
    new_ad(client, "Черновик", image=False)
    assert screen(client, pc) == []
    assert client.get("/api/admin/ads", headers=club["staff"]).json()["active_count"] == 0


def test_only_active_banners_inside_their_window_are_shown_in_rotation_order(env):
    client, club, pc = env
    enable(client, club)
    t = now_utc()
    c = new_ad(client, "Третий", sort_order=30, duration_seconds=20)
    a = new_ad(client, "Первый", sort_order=10, duration_seconds=5)
    new_ad(client, "Выключен", active=False)
    new_ad(client, "Ещё не начался", starts_at=(t + timedelta(days=1)).isoformat())
    new_ad(client, "Уже закончился", ends_at=(t - timedelta(days=1)).isoformat())
    b = new_ad(client, "Идёт сейчас", starts_at=(t - timedelta(days=1)).isoformat(), ends_at=(t + timedelta(days=1)).isoformat(), sort_order=20)
    shown = screen(client, pc)
    assert [x["id"] for x in shown] == [a["id"], b["id"], c["id"]]
    assert [x["seconds"] for x in shown] == [5, 10, 20]


def test_at_most_twelve_banners_go_to_the_rotation(env):
    client, club, pc = env
    enable(client, club)
    for i in range(15):
        new_ad(client, f"Баннер {i}", sort_order=i)
    assert len(screen(client, pc)) == 12


def test_the_picture_is_served_only_for_banners_that_are_really_running_in_this_club(client):
    a, b = make_club(client, "club-a"), make_club(client, "club-b", phone=PHONE_B)
    pc_a, pc_b = make_pc(client, a), make_pc(client, b)
    ad = new_ad(client)
    enable(client, a)
    url = f"/api/agent/ads/{ad['id']}/image"
    got = client.get(url, headers=pc_a["headers"])
    assert got.status_code == 200 and got.headers["content-type"] == "image/png" and got.content == png()
    assert got.headers["etag"] == f'"{ad["version"]}"'
    assert client.get(url, headers=pc_b["headers"]).status_code == 404       # в клубе Б реклама не включена
    assert client.get(url).status_code == 401                                # без ключа ПК
    assert client.get("/api/agent/ads/999/image", headers=pc_a["headers"]).status_code == 404
    client.patch(f"/api/platform/ads/{ad['id']}", json={"active": False}, headers=PLATFORM)
    assert client.get(url, headers=pc_a["headers"]).status_code == 404       # выключенный не скачать
    assert client.get("/api/agent/ads").status_code == 401
    assert screen(client, pc_b) == []


def test_platform_sets_the_clubs_share_and_the_club_sees_it(env):
    client, club, pc = env
    assert enable(client, club, enabled=True, share=40)["ads_club_share"] == 40
    assert client.get("/api/admin/ads", headers=club["staff"]).json()["club_share"] == 40
    for bad in (-1, 101):
        assert client.post(f"/api/platform/clubs/{club['id']}/ads", json={"club_share": bad}, headers=PLATFORM).status_code == 422
    assert client.post("/api/platform/clubs/999/ads", json={"enabled": True}, headers=PLATFORM).status_code == 404
    clubs = client.get("/api/platform/clubs", headers=PLATFORM).json()
    assert clubs[0]["ads_enabled"] is True and clubs[0]["ads_club_share"] == 40
    assert client.post("/api/admin/ads/enabled", json={"enabled": True, "club_share": 100}, headers=club["staff"]).json()["club_share"] == 40


# ---------- учёт времени показа ----------
def test_time_is_counted_on_the_server_from_pc_polls_in_chunks(env):
    client, club, pc = env
    ad1, ad2 = new_ad(client, "Раз"), new_ad(client, "Два")
    enable(client, club)
    client.get("/api/agent/state", headers=pc["headers"])                    # первый опрос только ставит метку
    assert stats() == []
    client.get("/api/agent/state", headers=pc["headers"])                    # прошло меньше 30 секунд: ничего
    assert stats() == []
    age_mark(pc["id"], 45)
    client.get("/api/agent/state", headers=pc["headers"])
    assert sorted(stats()) == sorted([(club["id"], ad1["id"], 45), (club["id"], ad2["id"], 45)])
    age_mark(pc["id"], 40)
    client.get("/api/agent/state", headers=pc["headers"])
    assert sorted(s for _, _, s in stats()) == [85, 85]


def test_a_long_gap_never_counts_more_than_a_minute_so_it_cannot_be_inflated(env):
    client, club, pc = env
    new_ad(client)
    enable(client, club)
    client.get("/api/agent/state", headers=pc["headers"])
    age_mark(pc["id"], 3 * 3600)
    client.get("/api/agent/state", headers=pc["headers"])
    assert [s for _, _, s in stats()] == [60]


@pytest.mark.parametrize("setup", ["disabled", "no_ads", "inactive", "no_picture"])
def test_nothing_is_counted_when_nothing_is_shown(env, setup):
    client, club, pc = env
    ad = new_ad(client, active=setup != "inactive", image=setup != "no_picture")
    if setup != "disabled":
        enable(client, club)
    if setup == "no_ads":
        client.patch(f"/api/platform/ads/{ad['id']}", json={"active": False}, headers=PLATFORM)
    client.get("/api/agent/state", headers=pc["headers"])
    with SessionLocal() as db:
        if db.get(PC, pc["id"]).ads_seen_at is not None:
            age_mark(pc["id"], 120)
    client.get("/api/agent/state", headers=pc["headers"])
    assert stats() == []


def test_time_counts_only_while_the_client_says_the_banner_is_on_screen(env):
    client, club, pc = env
    new_ad(client)
    enable(client, club)
    url = "/api/agent/state"
    client.get(url + "?ads=1", headers=pc["headers"])                        # заставка на экране: метка поставлена
    age_mark(pc["id"], 45)
    client.get(url + "?ads=0", headers=pc["headers"])                        # гость нажал мышь и смотрит форму входа: время не идёт, метка сброшена
    assert stats() == []
    client.get(url + "?ads=1", headers=pc["headers"])                        # заставка вернулась: снова только метка
    assert stats() == []
    age_mark(pc["id"], 45)
    client.get(url + "?ads=1", headers=pc["headers"])
    assert [s for _, _, s in stats()] == [45]
    age_mark(pc["id"], 45)
    client.get(url + "?ads=0", headers=pc["headers"])                        # а вот эти 45 секунд были у формы: в отчёт не попадают
    assert [s for _, _, s in stats()] == [45]


def test_an_old_client_without_the_parameter_is_counted_as_before(env):
    client, club, pc = env
    new_ad(client)
    enable(client, club)
    client.get("/api/agent/state", headers=pc["headers"])
    age_mark(pc["id"], 45)
    client.get("/api/agent/state", headers=pc["headers"])
    assert [s for _, _, s in stats()] == [45]


def test_playing_and_staff_screens_do_not_count_as_ad_time(env):
    client, club, pc = env
    new_ad(client)
    enable(client, club)
    tariff = make_tariff(client, club, name="Час", kind="hourly", price=100)
    guest = make_guest(client, club, pc, balance=500)
    client.get("/api/agent/state", headers=pc["headers"])
    age_mark(pc["id"], 120)
    assert start_for(client, club, guest, tariff).status_code == 200
    client.get("/api/agent/state", headers=pc["headers"])
    assert stats() == []
    with SessionLocal() as db:
        assert db.get(PC, pc["id"]).ads_seen_at is None


def test_ad_stats_are_split_by_club_day(client):
    a = make_club(client, "club-a", utc_offset=3)
    b = make_club(client, "club-b", phone=PHONE_B, utc_offset=-5)
    pc_a, pc_b = make_pc(client, a), make_pc(client, b)
    ad = new_ad(client)
    enable(client, a)
    enable(client, b)
    for pc in (pc_a, pc_b):
        client.get("/api/agent/state", headers=pc["headers"])
        age_mark(pc["id"], 60)
        client.get("/api/agent/state", headers=pc["headers"])
    assert sorted(stats()) == sorted([(a["id"], ad["id"], 60), (b["id"], ad["id"], 60)])
    with SessionLocal() as db:
        days = {s.club_id: s.day for s in db.scalars(select(AdStat))}
    assert days[a["id"]] == (now_utc() + timedelta(hours=3)).date() and days[b["id"]] == (now_utc() - timedelta(hours=5)).date()


# ---------- отчёт и удаление ----------
def test_report_shows_hours_per_banner_and_per_club(client):
    a, b = make_club(client, "club-a"), make_club(client, "club-b", phone=PHONE_B)
    pc_a, pc_b = make_pc(client, a), make_pc(client, b)
    ad1, ad2 = new_ad(client, "Раз"), new_ad(client, "Два")
    enable(client, a, share=25)
    enable(client, b)
    for pc, secs in ((pc_a, 60), (pc_b, 60)):
        client.get("/api/agent/state", headers=pc["headers"])
        age_mark(pc["id"], secs)
        client.get("/api/agent/state", headers=pc["headers"])
    age_mark(pc_a["id"], 60)
    client.get("/api/agent/state", headers=pc_a["headers"])

    report = client.get("/api/platform/ads/report", headers=PLATFORM).json()
    per_ad = {x["ad_id"]: x for x in report["per_ad"]}
    assert per_ad[ad1["id"]]["hours"] == round(180 / 3600, 2)
    assert {c["club"]: c["hours"] for c in per_ad[ad1["id"]]["clubs"]} == {a["name"]: round(120 / 3600, 2), b["name"]: round(60 / 3600, 2)}
    per_club = {x["club_id"]: x for x in report["per_club"]}
    assert per_club[a["id"]]["screen_hours"] == round(120 / 3600, 2) and per_club[a["id"]]["club_share"] == 25
    assert per_club[b["id"]]["screen_hours"] == round(60 / 3600, 2)
    assert client.get("/api/platform/ads", headers=PLATFORM).json()[0]["shown_hours"] == round(180 / 3600, 2)
    assert client.get("/api/admin/ads", headers=a["staff"]).json()["screen_hours_30d"] == round(120 / 3600, 2)
    assert client.get("/api/admin/ads", headers=b["staff"]).json()["screen_hours_30d"] == round(60 / 3600, 2)
    assert ad2["id"] in per_ad


def test_report_period_and_validation(env):
    client, club, pc = env
    new_ad(client)
    enable(client, club)
    client.get("/api/agent/state", headers=pc["headers"])
    age_mark(pc["id"], 60)
    client.get("/api/agent/state", headers=pc["headers"])
    today = date.today()
    far = client.get(f"/api/platform/ads/report?since={today - timedelta(days=60)}&until={today - timedelta(days=40)}", headers=PLATFORM).json()
    assert far["per_ad"] == [] and far["per_club"] == []
    assert client.get(f"/api/platform/ads/report?since={today}&until={today - timedelta(days=1)}", headers=PLATFORM).status_code == 422


def test_a_banner_with_recorded_views_cannot_be_deleted_only_switched_off(env):
    client, club, pc = env
    ad = new_ad(client)
    enable(client, club)
    client.get("/api/agent/state", headers=pc["headers"])
    age_mark(pc["id"], 60)
    client.get("/api/agent/state", headers=pc["headers"])
    assert client.delete(f"/api/platform/ads/{ad['id']}", headers=PLATFORM).status_code == 409
    assert client.patch(f"/api/platform/ads/{ad['id']}", json={"active": False}, headers=PLATFORM).status_code == 200
    assert client.delete("/api/platform/ads/999", headers=PLATFORM).status_code == 404
