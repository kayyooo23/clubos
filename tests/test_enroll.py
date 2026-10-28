"""Подключение ПК по коду: сотрудник создаёт код, служба на ПК подключается сама и получает свой ключ; код ограничен по сроку, числу ПК и попыткам;
ПК привязан к железу, а скопированный образ диска получает отказ и подключается заново как отдельный ПК."""
import re
from datetime import timedelta

import pytest

from app.db import SessionLocal
from app.models import Club, EnrollCode

from .helpers import GUEST1, PHONE_B, make_club, make_guest, make_pc, make_tariff

CODE_RE = re.compile(r"^[A-HJ-NP-Z2-9]{4}-[A-HJ-NP-Z2-9]{4}-[A-HJ-NP-Z2-9]{4}$")
MACHINE_A = "a1b2c3d4e5f60718293a4b5c6d7e8f90"
MACHINE_B = "b2c3d4e5f60718293a4b5c6d7e8f90a1"
MACHINE_C = "c3d4e5f60718293a4b5c6d7e8f90a1b2"


@pytest.fixture()
def env(client):
    return client, make_club(client)


def new_code(client, club, **body):
    r = client.post("/api/admin/enroll-codes", json=body, headers=club["staff"])
    assert r.status_code == 200, r.text
    return r.json()


def enroll(client, code, machine=MACHINE_A, hostname="DESKTOP-AB12"):
    return client.post("/api/agent/enroll", json={"code": code, "machine_id": machine, "hostname": hostname})


def state(client, key, machine=None):
    headers = {"X-PC-Key": key, **({"X-Machine-Id": machine} if machine else {})}
    return client.get("/api/agent/state", headers=headers)


def pcs(client, club):
    return client.get("/api/admin/pcs", headers=club["staff"]).json()


# ---------- коды ----------
def test_a_code_is_shown_once_in_a_readable_form_and_listed_without_its_text(env):
    client, club = env
    created = new_code(client, club, label="Зал 1", days=3, max_uses=30)
    assert CODE_RE.match(created["code"]) and created["status"] == "active" and created["max_uses"] == 30 and created["used_count"] == 0
    listed = client.get("/api/admin/enroll-codes", headers=club["staff"]).json()
    assert [c["id"] for c in listed] == [created["id"]] and "code" not in listed[0]
    assert listed[0]["hint"] == created["code"].replace("-", "")[-4:] and listed[0]["label"] == "Зал 1"
    assert new_code(client, club)["code"] != created["code"]


def test_only_staff_of_the_club_can_manage_codes(env):
    client, club = env
    pc = make_pc(client, club)
    guest = make_guest(client, club, pc, phone=GUEST1)
    assert client.post("/api/admin/enroll-codes", json={}, headers=guest["headers"]).status_code in (401, 403)
    assert client.post("/api/admin/enroll-codes", json={}).status_code in (401, 403)
    other = make_club(client, slug="club-b", phone=PHONE_B)
    mine = new_code(client, club)
    assert client.get("/api/admin/enroll-codes", headers=other["staff"]).json() == []
    assert client.delete(f"/api/admin/enroll-codes/{mine['id']}", headers=other["staff"]).status_code == 404
    assert enroll(client, mine["code"]).status_code == 200                              # у чужого клуба код не отобрал


def test_a_code_cannot_point_to_a_zone_of_another_club(env):
    client, club = env
    other = make_club(client, slug="club-b", phone=PHONE_B)
    theirs = client.post("/api/admin/zones", json={"name": "VIP"}, headers=other["staff"]).json()
    assert client.post("/api/admin/enroll-codes", json={"zone_id": theirs["id"]}, headers=club["staff"]).status_code == 404


# ---------- подключение ----------
def test_a_pc_enrolls_with_the_code_and_gets_its_own_working_key(env):
    client, club = env
    code = new_code(client, club)["code"]
    r = enroll(client, code)
    assert r.status_code == 200, r.text
    data = r.json()
    assert data["pc_name"] == "DESKTOP-AB12" and data["club_name"] == club["name"] and len(data["key"]) > 20
    assert state(client, data["key"]).status_code == 200                                 # ключ рабочий, вводить его руками не пришлось
    row = next(p for p in pcs(client, club) if p["id"] == data["pc_id"])
    assert row["enrolled_at"] is not None and row["zone"] == "Общий зал" and row["clone_alert_at"] is None
    assert client.get("/api/admin/enroll-codes", headers=club["staff"]).json()[0]["used_count"] == 1
    actions = [a["action"] for a in client.get("/api/admin/audit", headers=club["staff"]).json()]
    assert "pc_enrolled" in actions and "enroll_code_create" in actions


def test_the_code_is_accepted_in_any_case_and_without_dashes(env):
    client, club = env
    code = new_code(client, club)["code"]
    assert enroll(client, code.lower().replace("-", " ")).status_code == 200


def test_pcs_with_the_same_computer_name_get_different_names(env):
    client, club = env
    code = new_code(client, club)["code"]
    names = [enroll(client, code, machine=m, hostname="PC-1").json()["pc_name"] for m in (MACHINE_A, MACHINE_B, MACHINE_C)]
    assert names == ["PC-1", "PC-1-2", "PC-1-3"]
    assert enroll(client, code, machine="d4e5f60718293a4b5c6d7e8f90a1b2c3", hostname="  <b>х</b> ").json()["pc_name"] == "bb"   # скобки, слэши, пробелы и кириллица из имени убраны


def test_the_same_computer_enrolling_again_gets_a_new_key_and_not_a_second_pc(env):
    client, club = env
    created = new_code(client, club, max_uses=1)
    first = enroll(client, created["code"]).json()
    again = enroll(client, created["code"])                                              # переустановили клиент, место по коду уже занято, но ПК тот же
    assert again.status_code == 200 and again.json()["pc_id"] == first["pc_id"]
    assert again.json()["key"] != first["key"]
    assert state(client, first["key"]).status_code == 401 and state(client, again.json()["key"]).status_code == 200
    assert len(pcs(client, club)) == 1
    assert client.get("/api/admin/enroll-codes", headers=club["staff"]).json()[0]["used_count"] == 1


def test_new_pcs_land_in_the_zone_of_the_code_and_see_its_tariffs(env):
    client, club = env
    vip = client.post("/api/admin/zones", json={"name": "VIP"}, headers=club["staff"]).json()
    client.post("/api/admin/tariffs", json={"name": "Час VIP", "kind": "hourly", "price": 200, "zone_id": vip["id"]}, headers=club["staff"])
    make_tariff(client, club, name="Час", price=100)
    code = new_code(client, club, zone_id=vip["id"])["code"]
    data = enroll(client, code).json()
    assert data["zone"] == "VIP"
    tariffs = client.get("/api/agent/tariffs", headers={"X-PC-Key": data["key"]}).json()
    assert [t["name"] for t in tariffs] == ["Час VIP"]


# ---------- ограничения ----------
def test_the_number_of_pcs_per_code_is_limited(env):
    client, club = env
    code = new_code(client, club, max_uses=2)["code"]
    assert enroll(client, code, machine=MACHINE_A).status_code == 200
    assert enroll(client, code, machine=MACHINE_B).status_code == 200
    r = enroll(client, code, machine=MACHINE_C)
    assert r.status_code == 403 and len(pcs(client, club)) == 2
    assert client.get("/api/admin/enroll-codes", headers=club["staff"]).json()[0]["status"] == "used_up"


def test_an_expired_revoked_unknown_code_or_suspended_club_is_refused_with_one_message(env):
    client, club = env
    expired, revoked, ok = new_code(client, club), new_code(client, club), new_code(client, club)
    with SessionLocal() as db:
        row = db.get(EnrollCode, expired["id"])
        row.expires_at = row.created_at - timedelta(hours=1)
        db.commit()
    assert client.delete(f"/api/admin/enroll-codes/{revoked['id']}", headers=club["staff"]).json()["status"] == "revoked"
    answers = {enroll(client, c).json()["detail"] for c in (expired["code"], revoked["code"], "AAAA-BBBB-CCCC")}
    assert len(answers) == 1 and all(enroll(client, c).status_code == 403 for c in (expired["code"], revoked["code"], "AAAA-BBBB-CCCC"))   # по ответу не понять, был ли такой код
    assert client.get("/api/admin/enroll-codes", headers=club["staff"]).json()[0]["status"] in ("active", "expired", "revoked")
    with SessionLocal() as db:
        db.get(Club, club["id"]).status = "suspended"
        db.commit()
    assert enroll(client, ok["code"]).status_code == 403


def test_wrong_codes_are_counted_and_guessing_is_stopped(env):
    client, club = env
    good = new_code(client, club)["code"]
    for _ in range(15):
        assert enroll(client, "ZZZZ-ZZZZ-ZZZZ").status_code == 403
    r = enroll(client, good)
    assert r.status_code == 429                                                          # после пятнадцати неудач подряд даже верный код с этого адреса ждёт


def test_the_machine_id_and_code_must_look_right(env):
    client, club = env
    code = new_code(client, club)["code"]
    assert client.post("/api/agent/enroll", json={"code": code, "machine_id": "short"}).status_code == 422
    assert client.post("/api/agent/enroll", json={"code": code, "machine_id": "x" * 65}).status_code == 422
    assert client.post("/api/agent/enroll", json={"code": "ab", "machine_id": MACHINE_A}).status_code == 422
    assert client.post("/api/agent/enroll", json={"code": code, "machine_id": "bad id with spaces!"}).status_code == 422


# ---------- привязка к железу ----------
def test_a_manually_created_pc_binds_to_the_first_machine_that_signs_requests(env):
    client, club = env
    pc = make_pc(client, club)
    assert state(client, pc["key"]).status_code == 200                                   # старый клиент без отпечатка работает как раньше
    assert state(client, pc["key"], MACHINE_A).status_code == 200                        # первый отпечаток запомнен
    assert state(client, pc["key"], MACHINE_A).status_code == 200
    r = state(client, pc["key"], MACHINE_B)
    assert r.status_code == 409 and r.json()["code"] == "machine_mismatch"
    row = pcs(client, club)[0]
    assert row["clone_alert_at"] is not None                                             # администратор видит, что с ключом пробовал другой компьютер
    assert state(client, pc["key"], MACHINE_A).status_code == 200                        # настоящий компьютер продолжает работать


def test_a_cloned_pc_is_refused_and_enrolls_again_as_a_separate_pc(env):
    client, club = env
    code = new_code(client, club)["code"]
    original = enroll(client, code, machine=MACHINE_A, hostname="CLUB-PC").json()
    assert state(client, original["key"], MACHINE_A).status_code == 200
    clone = state(client, original["key"], MACHINE_B)                                    # тот же образ диска на другом железе
    assert clone.status_code == 409 and clone.json()["code"] == "machine_mismatch"
    again = enroll(client, code, machine=MACHINE_B, hostname="CLUB-PC").json()
    assert again["pc_id"] != original["pc_id"] and again["key"] != original["key"] and again["pc_name"] == "CLUB-PC-2"
    assert state(client, again["key"], MACHINE_B).status_code == 200 and state(client, original["key"], MACHINE_A).status_code == 200
    assert len(pcs(client, club)) == 2


def test_rotating_the_key_of_a_manual_pc_lets_it_move_to_other_hardware(env):
    client, club = env
    pc = make_pc(client, club)
    assert state(client, pc["key"], MACHINE_A).status_code == 200
    rotated = client.post(f"/api/admin/pcs/{pc['id']}/rotate-key", headers=club["staff"]).json()
    assert state(client, pc["key"], MACHINE_A).status_code == 401
    assert state(client, rotated["key"], MACHINE_B).status_code == 200                   # новый ключ вписан на другом компьютере: привязка к прежнему снята


def test_rotating_the_key_of_an_enrolled_pc_keeps_its_hardware_and_the_service_gets_a_new_key_by_itself(env):
    client, club = env
    code = new_code(client, club)["code"]
    pc = enroll(client, code, machine=MACHINE_A).json()
    client.post(f"/api/admin/pcs/{pc['pc_id']}/rotate-key", headers=club["staff"])
    assert state(client, pc["key"], MACHINE_A).status_code == 401
    again = enroll(client, code, machine=MACHINE_A).json()                               # служба, получив 401, подключается заново с того же компьютера
    assert again["pc_id"] == pc["pc_id"] and state(client, again["key"], MACHINE_A).status_code == 200 and len(pcs(client, club)) == 1


def test_staff_can_dismiss_the_clone_alert_and_only_in_their_own_club(env):
    client, club = env
    pc = enroll(client, new_code(client, club)["code"], machine=MACHINE_A).json()
    state(client, pc["key"], MACHINE_B)
    other = make_club(client, slug="club-b", phone=PHONE_B)
    assert client.post(f"/api/admin/pcs/{pc['pc_id']}/ack-clone", headers=other["staff"]).status_code == 404
    assert pcs(client, club)[0]["clone_alert_at"] is not None
    assert client.post(f"/api/admin/pcs/{pc['pc_id']}/ack-clone", headers=club["staff"]).status_code == 200
    assert pcs(client, club)[0]["clone_alert_at"] is None
    assert "pc_clone_ack" in [a["action"] for a in client.get("/api/admin/audit", headers=club["staff"]).json()]


def test_re_enrolling_clears_the_clone_alert(env):
    client, club = env
    code = new_code(client, club)["code"]
    pc = enroll(client, code, machine=MACHINE_A).json()
    state(client, pc["key"], MACHINE_B)
    assert pcs(client, club)[0]["clone_alert_at"] is not None
    enroll(client, code, machine=MACHINE_A)                                              # настоящий компьютер подключился заново
    assert pcs(client, club)[0]["clone_alert_at"] is None
