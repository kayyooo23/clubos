"""Залы как шаблоны: обычный зал, VIP, Premium, PS5, Duo. У зала свои характеристики и тарифы; ПК, добавленный в зал, ничего не вводит заново.
Ключевое правило: если у зала есть свои действующие тарифы, ПК зала видят только их, иначе общие тарифы клуба."""
import pytest

from .helpers import GUEST1, PHONE_B, make_club, make_guest, make_pc, make_tariff, start_for

VIP = {"name": "VIP", "cpu": "Intel Core i7-14700K", "gpu": "RTX 4070 Super", "ram": "32 ГБ", "monitor": "27\" 240 Гц", "peripherals": "Logitech G Pro, HyperX Cloud"}
STANDARD = {"name": "Обычный зал", "cpu": "Intel Core i5-12400F", "gpu": "RTX 3060", "ram": "16 ГБ", "monitor": "24\" 165 Гц"}


@pytest.fixture()
def env(client):
    club = make_club(client)
    return client, club


def zone(client, club, **body):
    r = client.post("/api/admin/zones", json=body, headers=club["staff"])
    assert r.status_code == 200, r.text
    return r.json()


def tariff(client, club, zone_id=None, **over):
    body = {"name": "Час", "kind": "hourly", "price": 100, **over}
    if zone_id is not None:
        body["zone_id"] = zone_id
    r = client.post("/api/admin/tariffs", json=body, headers=club["staff"])
    assert r.status_code == 200, r.text
    return r.json()


def add_pc(client, club, name, zone_id=None):
    body = {"name": name, **({"zone_id": zone_id} if zone_id is not None else {})}
    r = client.post("/api/admin/pcs", json=body, headers=club["staff"])
    assert r.status_code == 200, r.text
    data = r.json()
    return {**data, "headers": {"X-PC-Key": data["key"]}}


def agent_tariffs(client, pc):
    r = client.get("/api/agent/tariffs", headers=pc["headers"])
    assert r.status_code == 200, r.text
    return sorted((t["name"], t["price"]) for t in r.json())


def pc_row(client, club, pc_id):
    return next(p for p in client.get("/api/admin/pcs", headers=club["staff"]).json() if p["id"] == pc_id)


# ---------- залы ----------
def test_zones_are_created_listed_edited_and_ordered(env):
    client, club = env
    vip = zone(client, club, **VIP, sort_order=20)
    std = zone(client, club, **STANDARD, sort_order=10)
    assert vip["gpu"] == "RTX 4070 Super" and vip["pcs"] == 0 and vip["tariffs"] == 0
    assert [z["name"] for z in client.get("/api/admin/zones", headers=club["staff"]).json()] == ["Обычный зал", "VIP"]   # по порядку, потом по номеру
    r = client.patch(f"/api/admin/zones/{vip['id']}", json={"gpu": "RTX 4080", "peripherals": ""}, headers=club["staff"])
    assert r.status_code == 200 and r.json()["gpu"] == "RTX 4080" and r.json()["peripherals"] is None and r.json()["cpu"] == VIP["cpu"]
    assert std["id"] != vip["id"]


def test_ps5_and_duo_need_nothing_but_a_name(env):
    client, club = env
    ps5 = zone(client, club, name="PS5", note="PlayStation 5, два геймпада, телевизор 55\"")
    duo = zone(client, club, name="Duo")
    assert ps5["note"].startswith("PlayStation 5") and ps5["cpu"] is None and duo["name"] == "Duo"


def test_zone_names_are_unique_per_club_and_cleaned(env):
    client, club = env
    zone(client, club, name="  VIP  ", cpu="  i7  ")
    z = client.get("/api/admin/zones", headers=club["staff"]).json()[0]
    assert z["name"] == "VIP" and z["cpu"] == "i7"
    assert client.post("/api/admin/zones", json={"name": "VIP"}, headers=club["staff"]).status_code == 409
    assert client.post("/api/admin/zones", json={"name": "   "}, headers=club["staff"]).status_code == 422
    other = make_club(client, slug="club-b", phone=PHONE_B)
    assert client.post("/api/admin/zones", json={"name": "VIP"}, headers=other["staff"]).status_code == 200      # в другом клубе то же название можно


def test_renaming_a_zone_renames_it_on_the_map_for_its_pcs(env):
    client, club = env
    vip = zone(client, club, **VIP)
    pc = add_pc(client, club, "VIP-1", vip["id"])
    assert pc_row(client, club, pc["id"])["zone"] == "VIP"
    r = client.patch(f"/api/admin/zones/{vip['id']}", json={"name": "Premium VIP"}, headers=club["staff"])
    assert r.status_code == 200
    assert pc_row(client, club, pc["id"])["zone"] == "Premium VIP"
    zone(client, club, name="Duo")
    duo = next(z for z in client.get("/api/admin/zones", headers=club["staff"]).json() if z["name"] == "Duo")
    assert client.patch(f"/api/admin/zones/{duo['id']}", json={"name": "Premium VIP"}, headers=club["staff"]).status_code == 409


def test_a_zone_with_pcs_cannot_be_deleted_but_an_empty_one_can_and_takes_its_tariffs_with_it(env):
    client, club = env
    vip, duo = zone(client, club, name="VIP"), zone(client, club, name="Duo")
    pc = add_pc(client, club, "VIP-1", vip["id"])
    tariff(client, club, vip["id"], name="Час VIP", price=200)
    assert client.delete(f"/api/admin/zones/{vip['id']}", headers=club["staff"]).status_code == 409
    tariff(client, club, duo["id"], name="Час Duo", price=300)
    assert client.delete(f"/api/admin/zones/{duo['id']}", headers=club["staff"]).status_code == 200
    names = [t["name"] for t in client.get("/api/admin/tariffs", headers=club["staff"]).json()]
    assert names == ["Час VIP"]                                                    # тарифы удалённого зала отключены, чужие на месте
    assert pc["zone_id"] == vip["id"]


# ---------- ПК по шаблону ----------
def test_a_pc_added_with_a_zone_takes_its_name_and_the_zone_prices_without_typing_anything(env):
    client, club = env
    vip = zone(client, club, **VIP)
    tariff(client, club, vip["id"], name="Час VIP", price=200)
    tariff(client, club, vip["id"], name="3 часа VIP", kind="package", price=500, package_minutes=180)
    pc = add_pc(client, club, "VIP-1", vip["id"])
    assert pc["zone"] == "VIP" and pc["zone_id"] == vip["id"]
    assert agent_tariffs(client, pc) == [("3 часа VIP", 500), ("Час VIP", 200)]
    row = pc_row(client, club, pc["id"])
    assert row["zone"] == "VIP" and row["zone_id"] == vip["id"] and row["pos_x"] is None       # на карте ПК лежит «в стопке» зала, клетку выбирают потом


def test_a_pc_without_a_zone_keeps_working_with_the_common_tariffs(env):
    client, club = env
    tariff(client, club, name="Час", price=120)
    pc = add_pc(client, club, "PC-1")
    assert pc["zone_id"] is None and pc["zone"] == "Общий зал"
    assert agent_tariffs(client, pc) == [("Час", 120)]


def test_adding_a_pc_to_a_zone_of_another_club_is_refused(env):
    client, club = env
    other = make_club(client, slug="club-b", phone=PHONE_B)
    theirs = zone(client, other, name="VIP")
    r = client.post("/api/admin/pcs", json={"name": "PC-1", "zone_id": theirs["id"]}, headers=club["staff"])
    assert r.status_code == 404
    assert client.get("/api/admin/pcs", headers=club["staff"]).json() == []                # ПК не создан


# ---------- какие тарифы видит ПК ----------
def test_a_zone_with_its_own_tariffs_shows_only_them_and_other_zones_stay_on_the_common_ones(env):
    client, club = env
    tariff(client, club, name="Час", price=120)
    tariff(client, club, name="Ночь", kind="night", price=400, night_start=23, night_end=8)
    vip = zone(client, club, name="VIP")
    std = zone(client, club, name="Обычный зал")
    tariff(client, club, vip["id"], name="Час VIP", price=200)
    vip_pc, std_pc = add_pc(client, club, "VIP-1", vip["id"]), add_pc(client, club, "PC-1", std["id"])
    assert agent_tariffs(client, vip_pc) == [("Час VIP", 200)]                                 # общий «Час» рядом не показывается
    assert agent_tariffs(client, std_pc) == [("Ночь", 400), ("Час", 120)]                       # у обычного зала своих нет: общие


def test_disabling_the_last_zone_tariff_returns_the_zone_to_the_common_ones(env):
    client, club = env
    tariff(client, club, name="Час", price=120)
    vip = zone(client, club, name="VIP")
    own = tariff(client, club, vip["id"], name="Час VIP", price=200)
    pc = add_pc(client, club, "VIP-1", vip["id"])
    assert agent_tariffs(client, pc) == [("Час VIP", 200)]
    assert client.delete(f"/api/admin/tariffs/{own['id']}", headers=club["staff"]).status_code == 200
    assert agent_tariffs(client, pc) == [("Час", 120)]


def test_the_admin_list_for_a_pc_is_filtered_the_same_way(env):
    client, club = env
    tariff(client, club, name="Час", price=120)
    vip = zone(client, club, name="VIP")
    tariff(client, club, vip["id"], name="Час VIP", price=200)
    vip_pc, plain = add_pc(client, club, "VIP-1", vip["id"]), add_pc(client, club, "PC-1")
    for_vip = client.get(f"/api/admin/tariffs?pc_id={vip_pc['id']}", headers=club["staff"]).json()
    for_plain = client.get(f"/api/admin/tariffs?pc_id={plain['id']}", headers=club["staff"]).json()
    assert [t["name"] for t in for_vip] == ["Час VIP"] and [t["name"] for t in for_plain] == ["Час"]
    assert {t["name"] for t in client.get("/api/admin/tariffs", headers=club["staff"]).json()} == {"Час", "Час VIP"}   # без pc_id: все тарифы клуба
    assert {t["name"]: t["zone_id"] for t in client.get("/api/admin/tariffs", headers=club["staff"]).json()} == {"Час": None, "Час VIP": vip["id"]}


def test_a_tariff_cannot_be_put_into_a_zone_of_another_club(env):
    client, club = env
    other = make_club(client, slug="club-b", phone=PHONE_B)
    theirs = zone(client, other, name="VIP")
    r = client.post("/api/admin/tariffs", json={"name": "Час", "kind": "hourly", "price": 100, "zone_id": theirs["id"]}, headers=club["staff"])
    assert r.status_code == 404


# ---------- биллинг по залам ----------
def test_a_session_can_only_use_a_tariff_of_the_pcs_own_zone(env):
    client, club = env
    common = tariff(client, club, name="Час", price=120)
    vip = zone(client, club, name="VIP")
    vip_tariff = tariff(client, club, vip["id"], name="Час VIP", price=200)
    vip_pc, plain_pc = add_pc(client, club, "VIP-1", vip["id"]), add_pc(client, club, "PC-1")
    guest = make_guest(client, club, vip_pc, balance=1000)
    assert start_for(client, club, guest, common).status_code == 404                           # общий тариф в VIP не действует (там свои)
    other_guest = make_guest(client, club, plain_pc, phone="+79261234567", balance=1000)
    assert start_for(client, club, other_guest, vip_tariff).status_code == 404                  # а тариф VIP не действует на обычном ПК
    assert start_for(client, club, guest, vip_tariff).status_code == 200


def test_the_guest_pays_the_zone_price_for_the_session(env):
    client, club = env
    vip = zone(client, club, name="VIP")
    pack = tariff(client, club, vip["id"], name="3 часа VIP", kind="package", price=500, package_minutes=180)
    pc = add_pc(client, club, "VIP-1", vip["id"])
    guest = make_guest(client, club, pc, balance=800)
    r = client.post("/api/me/sessions", json={"tariff_id": pack["id"]}, headers=guest["headers"])
    assert r.status_code == 200, r.text
    assert next(a for a in client.get("/api/admin/accounts", headers=club["staff"]).json() if a["id"] == guest["id"])["balance"] == 300


def test_the_overtime_rate_after_a_package_is_the_hourly_price_of_the_zone(env):
    client, club = env
    tariff(client, club, name="Час", price=120)                                                # общий почасовой
    vip = zone(client, club, name="VIP")
    tariff(client, club, vip["id"], name="Час VIP", price=240)
    pack = tariff(client, club, vip["id"], name="3 часа VIP", kind="package", price=500, package_minutes=180)
    pc = add_pc(client, club, "VIP-1", vip["id"])
    guest = make_guest(client, club, pc, balance=1000)
    s = client.post("/api/me/sessions", json={"tariff_id": pack["id"]}, headers=guest["headers"]).json()
    assert s["overtime_price"] == 240                                                          # ставка VIP, а не общая 120


def test_extension_accepts_only_packages_of_the_pcs_zone(env):
    client, club = env
    common_pack = tariff(client, club, name="3 часа", kind="package", price=250, package_minutes=180)
    vip = zone(client, club, name="VIP")
    vip_pack = tariff(client, club, vip["id"], name="3 часа VIP", kind="package", price=500, package_minutes=180)
    pc = add_pc(client, club, "VIP-1", vip["id"])
    guest = make_guest(client, club, pc, balance=2000)
    assert client.post("/api/me/sessions", json={"tariff_id": vip_pack["id"]}, headers=guest["headers"]).status_code == 200
    assert client.post("/api/me/sessions/extend", json={"tariff_id": common_pack["id"]}, headers=guest["headers"]).status_code == 404
    assert client.post("/api/me/sessions/extend", json={"tariff_id": vip_pack["id"]}, headers=guest["headers"]).status_code == 200


# ---------- копирование тарифов ----------
def test_a_new_zone_is_filled_from_another_zone_in_one_click_and_then_prices_are_edited(env):
    client, club = env
    std = zone(client, club, name="Обычный зал")
    tariff(client, club, std["id"], name="Час", price=120)
    tariff(client, club, std["id"], name="3 часа", kind="package", price=300, package_minutes=180)
    premium = zone(client, club, name="Premium")
    r = client.post(f"/api/admin/zones/{premium['id']}/copy-tariffs", json={"from_zone_id": std["id"]}, headers=club["staff"])
    assert r.status_code == 200 and r.json() == {"copied": 2}
    mine = [t for t in client.get("/api/admin/tariffs", headers=club["staff"]).json() if t["zone_id"] == premium["id"]]
    assert sorted((t["name"], t["price"]) for t in mine) == [("3 часа", 300), ("Час", 120)]
    assert len([t for t in client.get("/api/admin/tariffs", headers=club["staff"]).json() if t["zone_id"] == std["id"]]) == 2   # у источника ничего не пропало


def test_copying_from_the_common_tariffs_and_the_refusals(env):
    client, club = env
    zone_a = zone(client, club, name="VIP")
    assert client.post(f"/api/admin/zones/{zone_a['id']}/copy-tariffs", json={}, headers=club["staff"]).status_code == 409    # копировать нечего
    tariff(client, club, name="Час", price=120)
    assert client.post(f"/api/admin/zones/{zone_a['id']}/copy-tariffs", json={}, headers=club["staff"]).json() == {"copied": 1}
    assert client.post(f"/api/admin/zones/{zone_a['id']}/copy-tariffs", json={}, headers=club["staff"]).status_code == 409    # свои тарифы уже есть
    zone_b = zone(client, club, name="Duo")
    assert client.post(f"/api/admin/zones/{zone_b['id']}/copy-tariffs", json={"from_zone_id": zone_b["id"]}, headers=club["staff"]).status_code == 422
    assert client.post(f"/api/admin/zones/{zone_b['id']}/copy-tariffs", json={"from_zone_id": 9999}, headers=club["staff"]).status_code == 404
    assert client.post("/api/admin/zones/9999/copy-tariffs", json={}, headers=club["staff"]).status_code == 404


# ---------- изменение тарифа ----------
def test_changing_a_tariff_creates_a_new_version_in_the_same_zone_and_keeps_the_old_one_for_running_sessions(env):
    client, club = env
    vip = zone(client, club, name="VIP")
    old = tariff(client, club, vip["id"], name="3 часа VIP", kind="package", price=500, package_minutes=180)
    pc = add_pc(client, club, "VIP-1", vip["id"])
    guest = make_guest(client, club, pc, balance=2000)
    assert client.post("/api/me/sessions", json={"tariff_id": old["id"]}, headers=guest["headers"]).status_code == 200
    body = {"name": "3 часа VIP", "kind": "package", "price": 550, "package_minutes": 180, "zone_id": 99999}      # зал в теле игнорируется: он остаётся прежним
    r = client.post(f"/api/admin/tariffs/{old['id']}/replace", json=body, headers=club["staff"])
    assert r.status_code == 200, r.text
    new = r.json()
    assert new["id"] != old["id"] and new["price"] == 550 and new["zone_id"] == vip["id"]
    assert agent_tariffs(client, pc) == [("3 часа VIP", 550)]                                                  # гостям показывается только новая версия
    active = client.get("/api/admin/sessions", headers=club["staff"]).json()
    assert active and active[0]["tariff"] == "3 часа VIP"                                                      # идущая сессия и отчёты не потеряли тариф
    assert client.post(f"/api/admin/tariffs/{old['id']}/replace", json=body, headers=club["staff"]).status_code == 409   # старая версия уже отключена


def test_changing_a_tariff_of_another_club_is_not_possible(env):
    client, club = env
    other = make_club(client, slug="club-b", phone=PHONE_B)
    theirs = tariff(client, other, name="Час", price=100)
    body = {"name": "Час", "kind": "hourly", "price": 1}
    assert client.post(f"/api/admin/tariffs/{theirs['id']}/replace", json=body, headers=club["staff"]).status_code == 404


# ---------- перенос ПК между залами ----------
def test_moving_a_pc_to_another_zone_changes_its_tariffs_and_clears_its_cell(env):
    client, club = env
    vip, std = zone(client, club, name="VIP"), zone(client, club, name="Обычный зал")
    tariff(client, club, vip["id"], name="Час VIP", price=200)
    tariff(client, club, std["id"], name="Час", price=120)
    pc = add_pc(client, club, "PC-1", std["id"])
    assert client.patch(f"/api/admin/pcs/{pc['id']}", json={"pos_x": 3, "pos_y": 2}, headers=club["staff"]).status_code == 200
    r = client.patch(f"/api/admin/pcs/{pc['id']}", json={"zone_id": vip["id"]}, headers=club["staff"])
    assert r.status_code == 200, r.text
    row = pc_row(client, club, pc["id"])
    assert row["zone"] == "VIP" and row["zone_id"] == vip["id"] and row["pos_x"] is None       # клетка была в сетке прежнего зала
    assert agent_tariffs(client, pc) == [("Час VIP", 200)]


def test_detaching_the_template_keeps_the_pc_in_its_map_zone_but_returns_the_common_tariffs(env):
    client, club = env
    tariff(client, club, name="Час", price=120)
    vip = zone(client, club, name="VIP")
    tariff(client, club, vip["id"], name="Час VIP", price=200)
    pc = add_pc(client, club, "VIP-1", vip["id"])
    assert client.patch(f"/api/admin/pcs/{pc['id']}", json={"zone_id": None}, headers=club["staff"]).status_code == 200
    row = pc_row(client, club, pc["id"])
    assert row["zone"] == "VIP" and row["zone_id"] is None
    assert agent_tariffs(client, pc) == [("Час", 120)]


def test_dragging_a_pc_into_another_zone_on_the_map_gives_it_that_zones_tariffs(env):
    client, club = env
    vip, std = zone(client, club, name="VIP"), zone(client, club, name="Обычный зал")
    tariff(client, club, vip["id"], name="Час VIP", price=200)
    tariff(client, club, std["id"], name="Час", price=120)
    pc = add_pc(client, club, "PC-1", std["id"])
    r = client.post("/api/admin/pcs/layout", json={"items": [{"id": pc["id"], "zone": "VIP", "pos_x": 1, "pos_y": 1}]}, headers=club["staff"])
    assert r.status_code == 200, r.text
    assert pc_row(client, club, pc["id"])["zone_id"] == vip["id"] and agent_tariffs(client, pc) == [("Час VIP", 200)]
    r = client.post("/api/admin/pcs/layout", json={"items": [{"id": pc["id"], "zone": "Бар", "pos_x": 2, "pos_y": 2}]}, headers=club["staff"])
    assert r.status_code == 200
    assert pc_row(client, club, pc["id"])["zone_id"] is None                                   # зала «Бар» как шаблона нет: общие тарифы


def test_a_pc_cannot_be_moved_into_a_zone_of_another_club(env):
    client, club = env
    other = make_club(client, slug="club-b", phone=PHONE_B)
    theirs = zone(client, other, name="VIP")
    pc = add_pc(client, club, "PC-1")
    assert client.patch(f"/api/admin/pcs/{pc['id']}", json={"zone_id": theirs["id"]}, headers=club["staff"]).status_code == 404


# ---------- изоляция клубов ----------
def test_a_club_never_sees_or_changes_another_clubs_zones(env):
    client, club = env
    other = make_club(client, slug="club-b", phone=PHONE_B)
    theirs = zone(client, other, name="VIP", cpu="секрет")
    assert client.get("/api/admin/zones", headers=club["staff"]).json() == []
    assert client.patch(f"/api/admin/zones/{theirs['id']}", json={"name": "Хак"}, headers=club["staff"]).status_code == 404
    assert client.delete(f"/api/admin/zones/{theirs['id']}", headers=club["staff"]).status_code == 404
    assert client.get("/api/admin/zones", headers=other["staff"]).json()[0]["name"] == "VIP"


def test_zone_actions_are_written_to_the_journal(env):
    client, club = env
    vip = zone(client, club, name="VIP")
    client.patch(f"/api/admin/zones/{vip['id']}", json={"cpu": "i7"}, headers=club["staff"])
    tariff(client, club, name="Час", price=120)
    client.post(f"/api/admin/zones/{vip['id']}/copy-tariffs", json={}, headers=club["staff"])
    client.delete(f"/api/admin/zones/{vip['id']}", headers=club["staff"])
    actions = [r["action"] for r in client.get("/api/admin/audit", headers=club["staff"]).json()]
    assert {"zone_create", "zone_update", "zone_tariffs_copy", "zone_delete"} <= set(actions)
