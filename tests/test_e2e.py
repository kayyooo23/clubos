"""Сквозной сценарий: платформа создаёт клуб → сотрудник настраивает ПК и тарифы → гость регистрируется по SMS,
пополняет баланс через кассу, играет → сотрудник открывает рабочий стол → отчёт и журнал сходятся."""
from .helpers import (GUEST1, GUEST_PASSWORD, PASSWORD, PHONE_A, PLATFORM, balance_of, ledger, shift_session, state)


def test_whole_club_day(client):
    # 1. платформа создаёт клуб, сотрудник входит в админку
    r = client.post("/api/platform/clubs", headers=PLATFORM, json={
        "slug": "e2e-club", "name": "E2E", "utc_offset": 3, "owner_phone": PHONE_A, "owner_password": PASSWORD})
    assert r.status_code == 200
    token = client.post("/api/admin/auth/login", json={"club": "e2e-club", "phone": PHONE_A, "password": PASSWORD}).json()["token"]
    staff = {"Authorization": f"Bearer {token}"}

    # 2. сотрудник заводит ПК и тарифы
    pcs = [client.post("/api/admin/pcs", json={"name": f"PC-{i}"}, headers=staff).json() for i in (1, 2)]
    pc1, pc2 = ({"id": p["id"], "headers": {"X-PC-Key": p["key"]}} for p in pcs)
    package = client.post("/api/admin/tariffs", headers=staff, json={
        "name": "3 часа", "kind": "package", "price": 250, "package_minutes": 180}).json()
    hourly = client.post("/api/admin/tariffs", headers=staff, json={"name": "Час", "kind": "hourly", "price": 100}).json()
    assert state(client, pc1)["mode"] == "locked"

    # 3. гость на ПК-1: видит тарифы, регистрируется по SMS
    assert {t["name"] for t in client.get("/api/agent/tariffs", headers=pc1["headers"]).json()} == {"3 часа", "Час"}
    assert client.post("/api/auth/register/request-code", json={"phone": "8 (903) 123-45-67"}, headers=pc1["headers"]).status_code == 200
    r = client.post("/api/auth/register/confirm", headers=pc1["headers"], json={
        "phone": GUEST1, "code": client.sms.last_code(GUEST1), "password": GUEST_PASSWORD, "accept_terms": True})
    assert r.status_code == 200
    guest = {**pc1["headers"], "Authorization": f"Bearer {r.json()['token']}"}
    guest_id = r.json()["account"]["id"]
    assert state(client, pc1)["mode"] == "locked" and state(client, pc1)["account"]["balance"] == 0

    # 4. денег нет — играть нельзя
    assert client.post("/api/me/sessions", json={"tariff_id": package["id"]}, headers=guest).status_code == 402

    # 5. гость платит наличными на кассе, сотрудник находит его по телефону и пополняет
    found = client.get("/api/admin/accounts", params={"phone": GUEST1}, headers=staff).json()
    assert [a["id"] for a in found] == [guest_id]
    assert client.post(f"/api/admin/accounts/{guest_id}/topup", json={"amount": 500}, headers=staff).json()["balance"] == 500
    assert state(client, pc1)["account"]["balance"] == 500                    # ПК гостя сразу видит новый баланс

    # 6. пакет: деньги списаны сразу, ПК разблокирован
    s1 = client.post("/api/me/sessions", json={"tariff_id": package["id"]}, headers=guest).json()
    assert state(client, pc1)["mode"] == "guest_session" and state(client, pc1)["account"]["balance"] == 250
    assert client.get("/api/admin/pcs", headers=staff).json()[0]["guest_phone"] == GUEST1
    assert client.post("/api/me/sessions/stop", headers=guest).json()["cost"] == 250
    assert state(client, pc1)["mode"] == "locked"

    # 7. почасовой: гость пополняет баланс прямо во время игры, потом уходит через полчаса
    s2 = client.post("/api/me/sessions", json={"tariff_id": hourly["id"]}, headers=guest).json()
    assert abs(s2["seconds_left"] - 150 * 60) <= 3                          # 250 ₽ при 100 ₽/ч = 2,5 часа
    client.post(f"/api/admin/accounts/{guest_id}/topup", json={"amount": 50}, headers=staff)
    assert abs(state(client, pc1)["session"]["seconds_left"] - 180 * 60) <= 3  # продлилось до 3 часов
    shift_session(s2["id"], minutes=30)
    done = client.post("/api/me/sessions/stop", headers=guest).json()
    assert done["cost"] in (50, 51) and done["balance"] == 300 - done["cost"]

    # 8. гость выходит; ПК снова чист
    assert client.post("/api/auth/logout", headers=guest).status_code == 200
    assert state(client, pc1) == {"mode": "locked", "account": None, "session": None}

    # 9. сотрудник открывает рабочий стол на ПК-2 и закрывает
    r = client.post("/api/auth/login", json={"phone": PHONE_A, "password": PASSWORD}, headers=pc2["headers"])
    assert r.json()["mode"] == "staff_desktop" and state(client, pc2)["mode"] == "staff_desktop"
    client.post("/api/auth/logout", headers={**pc2["headers"], "Authorization": f"Bearer {r.json()['token']}"})
    assert state(client, pc2)["mode"] == "locked"

    # 10. гость возвращается, входит на ПК-2 по телефону и паролю; баланс на месте
    r = client.post("/api/auth/login", json={"phone": GUEST1, "password": GUEST_PASSWORD}, headers=pc2["headers"])
    assert r.status_code == 200 and r.json()["account"]["balance"] == 300 - done["cost"]

    # 11. отчёт и журналы сходятся
    rep = client.get("/api/admin/report", params={"since": "2000-01-01T00:00:00"}, headers=staff).json()
    assert rep == {"sessions": 2, "revenue": 250 + done["cost"]}
    rows = list(reversed(ledger(client, {"staff": staff}, guest_id)))
    assert [r["kind"] for r in rows] == ["topup_cash", "session_charge", "topup_cash", "session_charge"]
    assert sum(r["amount"] for r in rows) == balance_of(client, {"staff": staff}, guest_id) == 300 - done["cost"]
    actions = {a["action"] for a in client.get("/api/admin/audit", headers=staff).json()}
    assert {"admin_login", "topup_cash", "staff_unlock", "staff_lock"} <= actions
