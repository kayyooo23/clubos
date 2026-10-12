"""Сообщения администратора гостю на экран ПК: отправка из админки, выдача ПК в состоянии, срок жизни, ограничения, изоляция клубов."""
from datetime import timedelta

import pytest

from app.db import SessionLocal
from app.models import PcNotice

from .helpers import GUEST1, PHONE_B, make_club, make_guest, make_pc, raw_state

QUIET = "Пожалуйста, постарайтесь вести себя потише."


@pytest.fixture()
def env(client):
    club = make_club(client)
    pc1, pc2 = make_pc(client, club, "PC-1"), make_pc(client, club, "PC-2")
    return client, club, pc1, pc2


def send(client, club, pc, text=QUIET):
    return client.post(f"/api/admin/pcs/{pc['id']}/notice", json={"text": text}, headers=club["staff"])


def age(notice_id, **delta):
    """«Прошло время»: сообщение отправлено раньше."""
    with SessionLocal() as db:
        n = db.get(PcNotice, notice_id)
        n.created_at -= timedelta(**delta)
        db.commit()


def test_a_message_reaches_the_chosen_pc_only(env):
    client, club, pc1, pc2 = env
    r = send(client, club, pc1)
    assert r.status_code == 200, r.text
    body = r.json()
    assert body["text"] == QUIET and body["delivered_at"] is None and body["online"] is False       # ПК ещё не опрашивал сервер
    assert raw_state(client, pc1)["notices"] == [{"id": body["id"], "text": QUIET}]
    assert "notices" not in raw_state(client, pc2)


def test_the_message_is_repeated_while_fresh_so_a_lost_poll_does_not_lose_it(env):
    client, club, pc1, _ = env
    nid = send(client, club, pc1).json()["id"]
    first = raw_state(client, pc1)["notices"]
    assert raw_state(client, pc1)["notices"] == first == [{"id": nid, "text": QUIET}]               # клиент сам покажет его один раз по номеру
    age(nid, minutes=3)
    assert "notices" not in raw_state(client, pc1)                                                   # устарело и больше не приходит


def test_the_admin_sees_when_the_pc_got_the_message(env):
    client, club, pc1, _ = env
    send(client, club, pc1)
    before = next(p for p in client.get("/api/admin/pcs", headers=club["staff"]).json() if p["name"] == "PC-1")
    assert before["last_notice"]["text"] == QUIET and before["last_notice"]["delivered_at"] is None
    raw_state(client, pc1)
    after = next(p for p in client.get("/api/admin/pcs", headers=club["staff"]).json() if p["name"] == "PC-1")
    assert after["last_notice"]["delivered_at"] is not None
    other = next(p for p in client.get("/api/admin/pcs", headers=club["staff"]).json() if p["name"] == "PC-2")
    assert other["last_notice"] is None


def test_online_is_reported_when_the_pc_has_just_polled(env):
    client, club, pc1, _ = env
    raw_state(client, pc1)
    assert send(client, club, pc1).json()["online"] is True


def test_the_pc_gets_the_last_three_messages_oldest_first(env):
    client, club, pc1, _ = env
    for i in range(5):
        send(client, club, pc1, text=f"Сообщение {i}")
    assert [n["text"] for n in raw_state(client, pc1)["notices"]] == ["Сообщение 2", "Сообщение 3", "Сообщение 4"]


def test_a_message_goes_to_a_guest_in_the_cabinet_too(env):
    client, club, pc1, _ = env
    make_guest(client, club, pc1, phone=GUEST1)
    send(client, club, pc1)
    state = raw_state(client, pc1)
    assert state["account"] is not None and state["notices"][0]["text"] == QUIET


def test_a_staff_desktop_gets_no_messages(env):
    client, club, pc1, _ = env
    assert client.post("/api/auth/login", json={"phone": club["phone"], "password": club["password"]}, headers=pc1["headers"]).status_code == 200
    send(client, club, pc1)
    assert raw_state(client, pc1)["mode"] == "staff_desktop" and "notices" not in raw_state(client, pc1)


# ---------- текст ----------
@pytest.mark.parametrize("text", ["", "   ", "\n\t "])
def test_an_empty_message_is_refused(env, text):
    client, club, pc1, _ = env
    assert send(client, club, pc1, text=text).status_code == 422


def test_the_length_limit_is_300_characters(env):
    client, club, pc1, _ = env
    assert send(client, club, pc1, text="а" * 300).status_code == 200
    r = send(client, club, pc1, text="а" * 301)
    assert r.status_code == 422 and "300" in r.json()["detail"]


def test_text_is_cleaned_but_line_breaks_stay(env):
    client, club, pc1, _ = env
    r = send(client, club, pc1, text="  Тише,\nпожалуйста\x00\x07  ")
    assert r.json()["text"] == "Тише,\nпожалуйста"


# ---------- безопасность ----------
def test_another_club_cannot_send_to_this_pc(env):
    client, club, pc1, _ = env
    other = make_club(client, slug="club-b", phone=PHONE_B)
    assert send(client, other, pc1).status_code == 404
    assert "notices" not in raw_state(client, pc1)


def test_a_guest_token_cannot_send_messages(env):
    client, club, pc1, _ = env
    guest = make_guest(client, club, pc1, phone=GUEST1)
    r = client.post(f"/api/admin/pcs/{pc1['id']}/notice", json={"text": QUIET}, headers=guest["headers"])
    assert r.status_code in (401, 403)
    assert client.post(f"/api/admin/pcs/{pc1['id']}/notice", json={"text": QUIET}).status_code in (401, 403)


def test_the_journal_records_who_sent_a_message_but_not_the_text(env):
    client, club, pc1, _ = env
    send(client, club, pc1)
    rows = client.get("/api/admin/audit", headers=club["staff"]).json()
    row = next(r for r in rows if r["action"] == "pc_notice")
    assert row["pc_id"] == pc1["id"] and QUIET not in (row["details"] or "")
