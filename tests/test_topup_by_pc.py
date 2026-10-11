"""Пополнение баланса по номеру ПК: сотрудник принял наличные у гостя, который сидит за этим компьютером."""
import pytest

from app import config

from .helpers import GUEST1, GUEST2, balance_of, ledger, make_club, make_guest, make_pc, make_tariff, start_for


@pytest.fixture()
def env(client):
    club = make_club(client)
    pc = make_pc(client, club)
    return client, club, pc


def topup_pc(client, club, pc, amount, comment=None):
    body = {"amount": amount}
    if comment is not None:
        body["comment"] = comment
    return client.post(f"/api/admin/pcs/{pc['id']}/topup", json=body, headers=club["staff"])


def test_who_sits_at_the_pc_is_the_guest_who_is_logged_in(env):
    client, club, pc = env
    guest = make_guest(client, club, pc, balance=150)
    r = client.get(f"/api/admin/pcs/{pc['id']}/guest", headers=club["staff"])
    assert r.status_code == 200
    body = r.json()
    assert body["guest"]["id"] == guest["id"] and body["guest"]["phone"] == GUEST1 and body["guest"]["balance"] == 150
    assert body["pc"]["name"] == "PC-1" and body["max_topup"] == config.MAX_TOPUP


def test_topup_by_pc_credits_the_guest_with_a_session(env):
    client, club, pc = env
    guest = make_guest(client, club, pc, balance=500)
    assert start_for(client, club, guest, make_tariff(client, club, price=100)).status_code == 200
    before = balance_of(client, club, guest["id"])
    r = topup_pc(client, club, pc, 300)
    assert r.status_code == 200, r.text
    assert r.json()["guest"]["balance"] == before + 300 and r.json()["amount"] == 300
    assert balance_of(client, club, guest["id"]) == before + 300
    last = ledger(client, club, guest["id"])[0]
    assert (last["amount"], last["kind"], last["comment"]) == (300, "topup_cash", "Пополнение за PC-1")
    assert last["staff_id"] is not None


def test_topup_by_pc_works_for_a_guest_who_only_opened_the_cabinet(env):
    client, club, pc = env
    guest = make_guest(client, club, pc)                      # вошёл в кабинет, сессию ещё не начал
    r = topup_pc(client, club, pc, 200, "перевод на карту")
    assert r.status_code == 200, r.text
    assert balance_of(client, club, guest["id"]) == 200
    assert ledger(client, club, guest["id"])[0]["comment"] == "перевод на карту"
    assert any(a["action"] == "topup_cash" for a in client.get("/api/admin/audit", headers=club["staff"]).json())


def test_the_guest_sees_the_new_balance_in_the_pc_state(env):
    client, club, pc = env
    guest = make_guest(client, club, pc)
    topup_pc(client, club, pc, 450)
    assert client.get("/api/me", headers=guest["headers"]).json()["balance"] == 450


def test_a_pc_without_a_guest_cannot_be_topped_up(env):
    client, club, pc = env
    r = client.get(f"/api/admin/pcs/{pc['id']}/guest", headers=club["staff"])
    assert r.json()["guest"] is None
    r = topup_pc(client, club, pc, 100)
    assert r.status_code == 409 and "нет гостя" in r.json()["detail"]


def test_another_guest_logged_in_elsewhere_is_not_credited(env):
    client, club, pc = env
    pc2 = make_pc(client, club, "PC-2")
    first = make_guest(client, club, pc)
    second = make_guest(client, club, pc2, phone=GUEST2)
    assert topup_pc(client, club, pc2, 100).status_code == 200
    assert balance_of(client, club, second["id"]) == 100
    assert balance_of(client, club, first["id"]) == 0          # деньги ушли только тому, кто за PC-2


@pytest.mark.parametrize("amount", [0, -5, config.MAX_TOPUP + 1, 10.5, "abc", None])
def test_bad_amounts_are_rejected_and_nothing_changes(env, amount):
    client, club, pc = env
    guest = make_guest(client, club, pc)
    assert topup_pc(client, club, pc, amount).status_code == 422
    assert balance_of(client, club, guest["id"]) == 0


def test_a_pc_of_another_club_is_not_found(env):
    client, club, pc = env
    other = make_club(client, slug="club-b", phone="+79161234567")
    foreign = make_pc(client, other, "PC-X")
    make_guest(client, other, foreign)
    assert client.get(f"/api/admin/pcs/{foreign['id']}/guest", headers=club["staff"]).status_code == 404
    assert topup_pc(client, club, foreign, 100).status_code == 404


def test_topup_by_pc_needs_a_staff_login(env):
    client, club, pc = env
    make_guest(client, club, pc)
    assert client.post(f"/api/admin/pcs/{pc['id']}/topup", json={"amount": 100}).status_code == 401
    assert client.get(f"/api/admin/pcs/{pc['id']}/guest").status_code == 401
