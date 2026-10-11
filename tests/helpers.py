"""Фабрики для тестов: клуб со сотрудником, ПК, тариф, гость."""
PLATFORM = {"X-Platform-Key": "platform-dev-key"}
PASSWORD = "correct-horse-1"          # пароль сотрудника
GUEST_PASSWORD = "guest-pass-123"
PHONE_A = "+79123456789"
PHONE_B = "+79161234567"
GUEST1 = "+79031234567"
GUEST2 = "+79261234567"
GUEST3 = "+79851234567"


def staff_login(client, slug, phone, password):
    r = client.post("/api/admin/auth/login", json={"club": slug, "phone": phone, "password": password})
    assert r.status_code == 200, r.text
    return r.json()["token"]


def make_club(client, slug="club-a", phone=PHONE_A, password=PASSWORD, utc_offset=3) -> dict:
    """Создаёт клуб через платформу и входит его сотрудником. Возвращает всё, что нужно тестам."""
    r = client.post("/api/platform/clubs", headers=PLATFORM, json={
        "slug": slug, "name": f"Клуб {slug}", "utc_offset": utc_offset,
        "owner_phone": phone, "owner_password": password})
    assert r.status_code == 200, r.text
    token = staff_login(client, slug, phone, password)
    return {**r.json(), "phone": phone, "password": password, "staff": {"Authorization": f"Bearer {token}"}}


def make_pc(client, club, name="PC-1") -> dict:
    r = client.post("/api/admin/pcs", json={"name": name}, headers=club["staff"])
    assert r.status_code == 200, r.text
    pc = r.json()
    return {"id": pc["id"], "key": pc["key"], "headers": {"X-PC-Key": pc["key"]}}


def make_tariff(client, club, **over) -> dict:
    body = {"name": "Час", "kind": "hourly", "price": 100, **over}
    r = client.post("/api/admin/tariffs", json=body, headers=club["staff"])
    assert r.status_code == 200, r.text
    return r.json()


def top_up(client, club, account_id, amount, comment=None):
    r = client.post(f"/api/admin/accounts/{account_id}/topup", json={"amount": amount, "comment": comment},
                    headers=club["staff"])
    assert r.status_code == 200, r.text
    return r.json()


def make_guest(client, club, pc, phone=GUEST1, password=GUEST_PASSWORD, balance=0) -> dict:
    """Регистрирует гостя на ПК pc (запрос кода → код из SMS → подтверждение) и при необходимости пополняет баланс.
    guest['headers'] — всё нужное для запросов гостя на этом ПК (ключ ПК + токен)."""
    r = client.post("/api/auth/register/request-code", json={"phone": phone}, headers=pc["headers"])
    assert r.status_code == 200, r.text
    r = client.post("/api/auth/register/confirm", headers=pc["headers"], json={
        "phone": phone, "code": client.sms.last_code(phone), "password": password, "accept_terms": True})
    assert r.status_code == 200, r.text
    data = r.json()
    guest = {"id": data["account"]["id"], "phone": phone, "password": password, "token": data["token"], "pc": pc,
             "headers": {**pc["headers"], "Authorization": f"Bearer {data['token']}"}}
    if balance:
        top_up(client, club, guest["id"], balance)
    return guest


def start_for(client, club, guest, tariff, pc=None):
    """Сотрудник стартует сессию за гостя (на ПК гостя по умолчанию)."""
    return client.post("/api/admin/sessions", headers=club["staff"], json={
        "pc_id": (pc or guest["pc"])["id"], "tariff_id": tariff["id"], "account_id": guest["id"]})


def raw_state(client, pc):
    return client.get("/api/agent/state", headers=pc["headers"]).json()


def state(client, pc):
    """Состояние ПК без названий клуба и ПК (они проверяются отдельным тестом)."""
    data = raw_state(client, pc)
    data.pop("club_name", None)
    data.pop("pc_name", None)
    return data


def shift_session(session_id, **delta):
    """«Прошло время»: сдвигаем начало (и запланированный конец) сессии назад, например hours=2."""
    from datetime import timedelta
    from app.db import SessionLocal
    from app.models import Session
    with SessionLocal() as db:
        row = db.get(Session, session_id)
        row.started_at -= timedelta(**delta)
        if row.planned_end:
            row.planned_end -= timedelta(**delta)
        if row.base_end:
            row.base_end -= timedelta(**delta)
        db.commit()


def balance_of(client, club, account_id) -> int:
    return next(a["balance"] for a in client.get("/api/admin/accounts", headers=club["staff"]).json() if a["id"] == account_id)


def ledger(client, club, account_id) -> list[dict]:
    return client.get(f"/api/admin/accounts/{account_id}/transactions", headers=club["staff"]).json()
