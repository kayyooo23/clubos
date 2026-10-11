"""Карта клуба: расстановка ПК, заметки, обслуживание, железо от клиента, дашборд."""
import pytest

from app.db import SessionLocal
from app.models import PC

from .helpers import (GUEST2, PHONE_B, make_club, make_guest, make_pc, make_tariff, start_for, state)

HW = {"cpu": "Intel Core i5-12400F", "ram_gb": 16, "gpu": "NVIDIA GeForce RTX 3060", "os": "Windows 11",
      "ip": "192.168.1.21", "mac": "00-1F-16-DD-4D-CB",
      "disks": [{"name": "C:", "total_gb": 476, "free_gb": 200}, {"name": "D:", "total_gb": 931, "free_gb": 400}]}


@pytest.fixture()
def env(client):
    club = make_club(client)
    pc1, pc2, pc3 = (make_pc(client, club, n) for n in ("PC-1", "PC-2", "PC-3"))
    return client, club, pc1, pc2, pc3


def pcs(client, club):
    return {p["name"]: p for p in client.get("/api/admin/pcs", headers=club["staff"]).json()}


def patch(client, club, pc, **body):
    return client.patch(f"/api/admin/pcs/{pc['id']}", json=body, headers=club["staff"])


# ---------- карта ----------
def test_new_pcs_start_in_the_common_hall_without_a_cell(env):
    client, club, pc1, *_ = env
    p = pcs(client, club)["PC-1"]
    assert (p["zone"], p["pos_x"], p["pos_y"], p["note"], p["maintenance"], p["hw"], p["hw_changed"]) == \
        ("Общий зал", None, None, None, False, None, False)


def test_pc_is_placed_on_the_map_renamed_and_annotated(env):
    client, club, pc1, pc2, _ = env
    r = patch(client, club, pc1, zone="VIP", pos_x=3, pos_y=1, note="  мышка залита  ", name="VIP-1")
    assert r.status_code == 200, r.text
    assert (r.json()["zone"], r.json()["pos_x"], r.json()["pos_y"], r.json()["note"], r.json()["name"]) == ("VIP", 3, 1, "мышка залита", "VIP-1")
    assert "VIP-1" in pcs(client, club)
    assert patch(client, club, pc1, note="").json()["note"] is None            # пустая заметка = нет заметки
    assert patch(client, club, pc2, name="VIP-1").status_code == 409           # имя занято
    assert patch(client, club, pc2, name="   ").status_code == 422


def test_two_pcs_cannot_share_a_cell_but_zones_have_their_own_grid(env):
    client, club, pc1, pc2, _ = env
    assert patch(client, club, pc1, zone="Зал", pos_x=2, pos_y=2).status_code == 200
    assert patch(client, club, pc2, zone="Зал", pos_x=2, pos_y=2).status_code == 409
    assert patch(client, club, pc2, zone="VIP", pos_x=2, pos_y=2).status_code == 200   # в другом зале та же клетка свободна
    assert patch(client, club, pc2, pos_x=5).json()["pos_x"] == 5                      # y остаётся прежним
    assert patch(client, club, pc2, pos_x=None, pos_y=None).json()["pos_x"] is None    # снять с карты


@pytest.mark.parametrize("body", [{"pos_x": 30, "pos_y": 0}, {"pos_x": -1, "pos_y": 0}, {"pos_x": 0, "pos_y": 20},
                                  {"pos_x": 3}, {"zone": ""}, {"club_id": 2}, {"hw_json": "x"}])
def test_bad_map_input_is_refused(env, body):
    client, club, pc1, *_ = env
    assert patch(client, club, pc1, **body).status_code in (422,)


def test_layout_is_saved_all_at_once_or_not_at_all(env):
    client, club, pc1, pc2, pc3 = env
    ok = client.post("/api/admin/pcs/layout", headers=club["staff"], json={"items": [
        {"id": pc1["id"], "zone": "Зал", "pos_x": 0, "pos_y": 0}, {"id": pc2["id"], "zone": "Зал", "pos_x": 1, "pos_y": 0},
        {"id": pc3["id"], "zone": "Зал", "pos_x": None, "pos_y": None}]})
    assert ok.status_code == 200 and ok.json() == {"saved": 3}
    # обмен местами двух ПК проходит, хотя по одному клетки бы пересекались
    swap = client.post("/api/admin/pcs/layout", headers=club["staff"], json={"items": [
        {"id": pc1["id"], "zone": "Зал", "pos_x": 1, "pos_y": 0}, {"id": pc2["id"], "zone": "Зал", "pos_x": 0, "pos_y": 0}]})
    assert swap.status_code == 200
    p = pcs(client, club)
    assert (p["PC-1"]["pos_x"], p["PC-2"]["pos_x"]) == (1, 0)
    # конфликт: ничего не меняется
    bad = client.post("/api/admin/pcs/layout", headers=club["staff"], json={"items": [
        {"id": pc1["id"], "zone": "Зал", "pos_x": 7, "pos_y": 7}, {"id": pc3["id"], "zone": "Зал", "pos_x": 0, "pos_y": 0}]})
    assert bad.status_code == 409
    assert pcs(client, club)["PC-1"]["pos_x"] == 1
    dup = client.post("/api/admin/pcs/layout", headers=club["staff"], json={"items": [
        {"id": pc1["id"], "zone": "Зал", "pos_x": 1, "pos_y": 1}, {"id": pc1["id"], "zone": "Зал", "pos_x": 2, "pos_y": 2}]})
    assert dup.status_code == 422


def test_map_is_isolated_between_clubs(client):
    a = make_club(client, "club-a")
    b = make_club(client, "club-b", phone=PHONE_B)
    pc_a = make_pc(client, a)
    assert patch(client, b, pc_a, zone="Чужой").status_code == 404
    r = client.post("/api/admin/pcs/layout", headers=b["staff"], json={"items": [{"id": pc_a["id"], "zone": "Z", "pos_x": 0, "pos_y": 0}]})
    assert r.status_code == 404
    assert client.post(f"/api/admin/pcs/{pc_a['id']}/ack-hardware", headers=b["staff"]).status_code == 404


# ---------- обслуживание ----------
def test_maintenance_blocks_guests_and_sessions_but_not_staff(env):
    client, club, pc1, pc2, _ = env
    tariff = make_tariff(client, club, name="Час", kind="hourly", price=100)
    guest = make_guest(client, club, pc1, balance=500)
    assert patch(client, club, pc2, maintenance=True).status_code == 200
    assert state(client, pc2) == {"mode": "locked", "account": None, "session": None, "message": "ПК на обслуживании"}

    # гость не войдёт и не зарегистрируется на ПК на обслуживании
    r = client.post("/api/auth/login", json={"phone": guest["phone"], "password": guest["password"]}, headers=pc2["headers"])
    assert r.status_code == 409 and "на обслуживании" in r.json()["detail"]
    # сессию за гостя на таком ПК тоже не запустить
    assert client.post("/api/admin/sessions", headers=club["staff"], json={
        "pc_id": pc2["id"], "tariff_id": tariff["id"], "account_id": guest["id"]}).status_code == 409
    # а сотрудник входит как обычно: ему нужно обслуживать ПК
    r = client.post("/api/admin/pcs/%d/lock" % pc2["id"], headers=club["staff"])
    assert r.status_code == 200
    # снимаем флаг: всё снова работает
    assert patch(client, club, pc2, maintenance=False).status_code == 200
    r = client.post("/api/auth/login", json={"phone": guest["phone"], "password": guest["password"]}, headers=pc2["headers"])
    assert r.status_code == 200


def test_maintenance_cannot_be_set_while_a_session_is_running(env):
    client, club, pc1, *_ = env
    tariff = make_tariff(client, club, name="Час", kind="hourly", price=100)
    guest = make_guest(client, club, pc1, balance=500)
    assert start_for(client, club, guest, tariff).status_code == 200
    assert patch(client, club, pc1, maintenance=True).status_code == 409
    assert pcs(client, club)["PC-1"]["maintenance"] is False


def test_maintenance_changes_are_audited(env):
    client, club, pc1, *_ = env
    patch(client, club, pc1, maintenance=True)
    patch(client, club, pc1, maintenance=False)
    patch(client, club, pc1, note="x")
    actions = [a["action"] for a in client.get("/api/admin/audit", headers=club["staff"]).json()]
    assert actions[:3] == ["pc_update", "pc_maintenance_off", "pc_maintenance_on"]


# ---------- железо ----------
def test_pc_reports_hardware_and_staff_sees_it_without_a_change_flag(env):
    client, club, pc1, *_ = env
    r = client.post("/api/agent/hardware", json=HW, headers=pc1["headers"])
    assert r.status_code == 200 and r.json() == {"ok": True, "changed": False}
    p = pcs(client, club)["PC-1"]
    assert p["hw"]["cpu"] == "Intel Core i5-12400F" and p["hw"]["ram_gb"] == 16 and p["hw"]["disks"][1]["name"] == "D:"
    assert p["hw"]["reported_at"] and p["hw_changed"] is False


def test_changed_configuration_is_flagged_until_staff_acknowledges(env):
    client, club, pc1, *_ = env
    client.post("/api/agent/hardware", json=HW, headers=pc1["headers"])
    # свободное место, IP и система меняются постоянно: это не «изменение конфигурации»
    noise = {**HW, "ip": "192.168.1.99", "os": "Windows 11 23H2", "disks": [{"name": "C:", "total_gb": 476, "free_gb": 10}, {"name": "D:", "total_gb": 931, "free_gb": 3}]}
    assert client.post("/api/agent/hardware", json=noise, headers=pc1["headers"]).json()["changed"] is False
    assert pcs(client, club)["PC-1"]["hw_changed"] is False
    # а видеокарту поменяли
    swapped = {**HW, "gpu": "NVIDIA GeForce RTX 4070"}
    assert client.post("/api/agent/hardware", json=swapped, headers=pc1["headers"]).json()["changed"] is True
    assert pcs(client, club)["PC-1"]["hw_changed"] is True
    assert client.post(f"/api/admin/pcs/{pc1['id']}/ack-hardware", headers=club["staff"]).status_code == 200
    assert pcs(client, club)["PC-1"]["hw_changed"] is False


@pytest.mark.parametrize("bad", [{"mac": "not-a-mac"}, {"ram_gb": -1}, {"cpu": "x" * 500},
                                 {"disks": [{"name": "C:", "total_gb": 1, "free_gb": 1}] * 9}, {"ip": "1" * 100}])
def test_bad_hardware_reports_are_refused(env, bad):
    client, club, pc1, *_ = env
    assert client.post("/api/agent/hardware", json={**HW, **bad}, headers=pc1["headers"]).status_code == 422


def test_hardware_report_needs_the_pc_key_and_stays_in_its_own_club(client):
    a = make_club(client, "club-a")
    b = make_club(client, "club-b", phone=PHONE_B)
    pc_a, pc_b = make_pc(client, a), make_pc(client, b)
    assert client.post("/api/agent/hardware", json=HW).status_code == 401
    assert client.post("/api/agent/hardware", json=HW, headers={"X-PC-Key": "wrong"}).status_code == 401
    client.post("/api/agent/hardware", json=HW, headers=pc_a["headers"])
    assert pcs(client, b)["PC-1"]["hw"] is None


# ---------- дашборд ----------
def test_dashboard_sums_up_the_day(env):
    client, club, pc1, pc2, pc3 = env
    tariff = make_tariff(client, club, name="3 часа", kind="package", price=250, package_minutes=180)
    for pc in (pc1, pc2):                                            # два ПК «на связи»
        client.get("/api/agent/state", headers=pc["headers"])
    guest = make_guest(client, club, pc1, balance=600)
    other = make_guest(client, club, pc2, phone=GUEST2, balance=300)
    assert start_for(client, club, guest, tariff).status_code == 200
    stop = client.post("/api/admin/sessions", headers=club["staff"], json={"pc_id": pc2["id"], "tariff_id": tariff["id"], "account_id": other["id"]})
    client.post(f"/api/admin/sessions/{stop.json()['id']}/stop", headers=club["staff"])   # закрытая сессия: 250 ₽ выручки
    patch(client, club, pc3, maintenance=True)

    d = client.get("/api/admin/dashboard", headers=club["staff"]).json()
    assert d["pcs_total"] == 3 and d["pcs_online"] == 2 and d["pcs_playing"] == 1 and d["pcs_maintenance"] == 1
    assert d["load_percent"] == 33
    assert d["sessions_today"] == 1 and d["games_revenue_today"] == 250
    assert d["cash_in_today"] == 900 and d["new_guests_today"] == 2
    assert d["shop_orders_today"] == 0 and d["orders_open"] == 0 and d["calls_open"] == 0


def test_dashboard_counts_the_bar_and_the_queue_and_needs_staff_rights(env):
    client, club, pc1, *_ = env
    guest = make_guest(client, club, pc1, balance=500)
    cola = client.post("/api/admin/products", headers=club["staff"], json={"name": "Кола", "price": 80}).json()
    client.post("/api/me/orders", headers=guest["headers"], json={"items": [{"product_id": cola["id"], "qty": 2}]})
    client.post("/api/me/calls", headers=guest["headers"], json={})
    d = client.get("/api/admin/dashboard", headers=club["staff"]).json()
    assert (d["shop_orders_today"], d["shop_revenue_today"], d["orders_open"], d["calls_open"]) == (1, 160, 1, 1)
    assert client.get("/api/admin/dashboard").status_code in (401, 403)
    assert client.get("/api/admin/dashboard", headers=guest["headers"]).status_code in (401, 403)


def test_dashboard_with_no_pcs_does_not_divide_by_zero(client):
    club = make_club(client)
    d = client.get("/api/admin/dashboard", headers=club["staff"]).json()
    assert d["pcs_total"] == 0 and d["load_percent"] == 0
