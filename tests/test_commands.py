"""Команды ПК от администратора: перезагрузить и выключить, одному ПК или многим. Доставка с очередным опросом, подтверждение, повторы, срок жизни, защита."""
from datetime import timedelta

import pytest

from app.db import SessionLocal
from app.models import PcCommand
from app.services import commands as cmd_service

from .helpers import GUEST1, PHONE_B, make_club, make_guest, make_pc, make_tariff, raw_state, start_for
from .test_bookings import now_utc


@pytest.fixture()
def env(client):
    club = make_club(client)
    pc1, pc2 = make_pc(client, club, "PC-1"), make_pc(client, club, "PC-2")
    hourly = make_tariff(client, club, name="Час", kind="hourly", price=100)
    return client, club, pc1, pc2, hourly


def send(client, club, pc, kind="reboot", **extra):
    return client.post(f"/api/admin/pcs/{pc['id']}/commands", json={"kind": kind, **extra}, headers=club["staff"])


def shift_command(command_id, **delta):
    """«Прошло время»: команда создана, отдана и истекает раньше."""
    with SessionLocal() as db:
        c = db.get(PcCommand, command_id)
        c.created_at -= timedelta(**delta)
        c.expires_at -= timedelta(**delta)
        if c.sent_at:
            c.sent_at -= timedelta(**delta)
        db.commit()


def listed(client, club, pc):
    return client.get(f"/api/admin/pcs/{pc['id']}/commands", headers=club["staff"]).json()


# ---------- постановка и доставка ----------
def test_a_command_waits_for_the_pc_and_arrives_with_the_next_poll(env):
    client, club, pc1, pc2, _ = env
    r = send(client, club, pc1)
    assert r.status_code == 200, r.text
    body = r.json()
    assert body["kind"] == "reboot" and body["status"] == "pending" and body["created"] is True and body["online"] is False and body["label"] == "Перезагрузка"
    state = raw_state(client, pc1)
    assert state["commands"] == [{"id": body["id"], "kind": "reboot", "payload": {"force": False}}]
    assert "commands" not in raw_state(client, pc2)                                      # другому ПК команда не уходит
    assert listed(client, club, pc1)[0]["status"] == "sent" and listed(client, club, pc1)[0]["attempts"] == 1


def test_a_delivered_command_is_not_repeated_at_once_but_is_resent_if_unconfirmed(env):
    client, club, pc1, *_ = env
    cid = send(client, club, pc1).json()["id"]
    assert len(raw_state(client, pc1)["commands"]) == 1
    assert "commands" not in raw_state(client, pc1)                                      # опрос раз в две секунды: та же команда второй раз сразу не нужна
    shift_command(cid, seconds=cmd_service.RESEND_AFTER_SECONDS + 1)
    assert [c["id"] for c in raw_state(client, pc1)["commands"]] == [cid]                # ответ мог потеряться: отдаём ещё раз
    shift_command(cid, seconds=cmd_service.RESEND_AFTER_SECONDS + 1)
    assert [c["id"] for c in raw_state(client, pc1)["commands"]] == [cid]
    shift_command(cid, seconds=cmd_service.RESEND_AFTER_SECONDS + 1)
    assert "commands" not in raw_state(client, pc1)                                      # три раза отдавали, подтверждения нет: хватит
    assert listed(client, club, pc1)[0]["attempts"] == 3


def test_the_result_closes_the_command_and_a_repeated_report_changes_nothing(env):
    client, club, pc1, pc2, _ = env
    cid = send(client, club, pc1, "shutdown").json()["id"]
    raw_state(client, pc1)
    r = client.post(f"/api/agent/commands/{cid}/result", json={"ok": True, "message": "Выключение через 30 секунд"}, headers=pc1["headers"])
    assert r.status_code == 200 and r.json()["status"] == "done"
    row = listed(client, club, pc1)[0]
    assert row["status"] == "done" and row["result"] == "Выключение через 30 секунд" and row["finished_at"] is not None
    again = client.post(f"/api/agent/commands/{cid}/result", json={"ok": False, "message": "поздно"}, headers=pc1["headers"])
    assert again.status_code == 200 and listed(client, club, pc1)[0]["status"] == "done"   # дубликат и запоздалый отказ ничего не меняют
    shift_command(cid, seconds=cmd_service.RESEND_AFTER_SECONDS + 1)
    assert "commands" not in raw_state(client, pc1)


def test_a_failure_is_recorded_with_its_reason(env):
    client, club, pc1, *_ = env
    cid = send(client, club, pc1).json()["id"]
    raw_state(client, pc1)
    client.post(f"/api/agent/commands/{cid}/result", json={"ok": False, "message": "Отказано в доступе"}, headers=pc1["headers"])
    row = listed(client, club, pc1)[0]
    assert row["status"] == "failed" and row["result"] == "Отказано в доступе"


def test_a_pc_cannot_report_on_a_command_of_another_pc(env):
    client, club, pc1, pc2, _ = env
    cid = send(client, club, pc1).json()["id"]
    r = client.post(f"/api/agent/commands/{cid}/result", json={"ok": True}, headers=pc2["headers"])
    assert r.status_code == 404 and listed(client, club, pc1)[0]["status"] == "pending"


def test_the_same_command_is_not_queued_twice(env):
    client, club, pc1, *_ = env
    first = send(client, club, pc1).json()
    second = send(client, club, pc1).json()
    assert second["id"] == first["id"] and second["created"] is False
    assert send(client, club, pc1, "shutdown").json()["id"] != first["id"]               # другая команда ставится отдельно
    assert len(listed(client, club, pc1)) == 2


# ---------- защита ----------
def test_a_command_over_a_running_session_needs_confirmation(env):
    client, club, pc1, pc2, hourly = env
    guest = make_guest(client, club, pc1, phone=GUEST1, balance=500)
    assert start_for(client, club, guest, hourly).status_code == 200
    refused = send(client, club, pc1)
    assert refused.status_code == 409 and "сессия" in refused.json()["detail"] and listed(client, club, pc1) == []
    forced = send(client, club, pc1, force=True)
    assert forced.status_code == 200 and raw_state(client, pc1)["commands"][0]["payload"] == {"force": True}   # решение администратора доходит до службы


def test_only_known_commands_are_accepted(env):
    client, club, pc1, *_ = env
    for bad in ("format", "run", "", "REBOOT"):
        assert send(client, club, pc1, bad).status_code == 422
    assert client.post(f"/api/admin/pcs/{pc1['id']}/commands", json={"kind": "reboot", "payload": {"cmd": "calc"}}, headers=club["staff"]).status_code == 200   # лишнее поле игнорируется


def test_an_old_command_expires_and_is_never_delivered(env):
    client, club, pc1, *_ = env
    cid = send(client, club, pc1).json()["id"]
    shift_command(cid, minutes=cmd_service.TTL_MINUTES + 1)
    assert "commands" not in raw_state(client, pc1)                                      # перезагрузка, поставленная давно, никому не нужна
    row = listed(client, club, pc1)[0]
    assert row["status"] == "cancelled" and "срок" in row["result"]
    assert send(client, club, pc1).json()["created"] is True                             # а новую поставить можно


def test_a_pending_command_can_be_cancelled_but_a_finished_one_cannot(env):
    client, club, pc1, *_ = env
    cid = send(client, club, pc1).json()["id"]
    r = client.post(f"/api/admin/commands/{cid}/cancel", headers=club["staff"])
    assert r.status_code == 200 and r.json()["status"] == "cancelled"
    assert "commands" not in raw_state(client, pc1)
    assert client.post(f"/api/admin/commands/{cid}/cancel", headers=club["staff"]).status_code == 409
    assert client.post("/api/admin/commands/99999/cancel", headers=club["staff"]).status_code == 404


def test_another_club_cannot_command_or_see_our_pcs(env):
    client, club, pc1, *_ = env
    other = make_club(client, slug="club-b", phone=PHONE_B)
    cid = send(client, club, pc1).json()["id"]
    assert send(client, other, pc1).status_code == 404
    assert client.get(f"/api/admin/pcs/{pc1['id']}/commands", headers=other["staff"]).status_code == 404
    assert client.post(f"/api/admin/commands/{cid}/cancel", headers=other["staff"]).status_code == 404
    bulk = client.post("/api/admin/commands", json={"pc_ids": [pc1["id"]], "kind": "reboot"}, headers=other["staff"]).json()
    assert bulk["queued"] == 0 and bulk["refused"] == 1


def test_a_guest_cannot_send_commands(env):
    client, club, pc1, *_ = env
    guest = make_guest(client, club, pc1, phone=GUEST1)
    assert client.post(f"/api/admin/pcs/{pc1['id']}/commands", json={"kind": "reboot"}, headers=guest["headers"]).status_code in (401, 403)
    assert client.post(f"/api/admin/pcs/{pc1['id']}/commands", json={"kind": "reboot"}).status_code in (401, 403)


# ---------- сразу несколько ПК ----------
def test_one_command_to_many_pcs_reports_each_one_and_refuses_only_the_busy(env):
    client, club, pc1, pc2, hourly = env
    guest = make_guest(client, club, pc1, phone=GUEST1, balance=500)
    start_for(client, club, guest, hourly)
    r = client.post("/api/admin/commands", json={"pc_ids": [pc1["id"], pc2["id"], pc2["id"], 99999], "kind": "shutdown"}, headers=club["staff"])
    assert r.status_code == 200, r.text
    out = r.json()
    by_pc = {x["pc_id"]: x for x in out["results"]}
    assert by_pc[pc1["id"]]["ok"] is False and "сессия" in by_pc[pc1["id"]]["error"]    # занятый отказан
    assert by_pc[pc2["id"]]["ok"] is True and by_pc[pc2["id"]]["created"] is True        # свободному поставлено, повтор в списке не плодит вторую
    assert by_pc[99999]["ok"] is False
    assert out["queued"] == 1 and out["refused"] == 2 and len(out["results"]) == 3
    assert raw_state(client, pc2)["commands"][0]["kind"] == "shutdown" and "commands" not in raw_state(client, pc1)


# ---------- видимость ----------
def test_the_command_reaches_a_pc_on_the_staff_desktop_too(env):
    client, club, pc1, *_ = env
    assert client.post("/api/auth/login", json={"phone": club["phone"], "password": club["password"]}, headers=pc1["headers"]).status_code == 200
    send(client, club, pc1)
    state = raw_state(client, pc1)
    assert state["mode"] == "staff_desktop" and state["commands"][0]["kind"] == "reboot"


def test_the_last_command_and_the_journal_are_visible_to_the_admin(env):
    client, club, pc1, pc2, _ = env
    cid = send(client, club, pc1).json()["id"]
    row = next(p for p in client.get("/api/admin/pcs", headers=club["staff"]).json() if p["id"] == pc1["id"])
    assert row["last_command"]["id"] == cid and row["last_command"]["status"] == "pending"
    assert next(p for p in client.get("/api/admin/pcs", headers=club["staff"]).json() if p["id"] == pc2["id"])["last_command"] is None
    client.post(f"/api/admin/commands/{cid}/cancel", headers=club["staff"])
    actions = [a["action"] for a in client.get("/api/admin/audit", headers=club["staff"]).json()]
    assert {"pc_command", "pc_command_cancel"} <= set(actions)
