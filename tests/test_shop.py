"""Бар и вызов администратора: меню, заказ с оплатой с баланса, отмена с возвратом, вызовы, изоляция клубов."""
from datetime import datetime, timedelta, timezone

import pytest

from .helpers import GUEST2, PHONE_B, balance_of, ledger, make_club, make_guest, make_pc, top_up

COLA = {"name": "Кола", "category": "Напитки", "price": 80}
BURGER = {"name": "Бургер", "category": "Еда", "price": 250}


@pytest.fixture()
def env(client):
    club = make_club(client)
    pc = make_pc(client, club)
    guest = make_guest(client, club, pc, balance=500)
    return client, club, pc, guest


def add_product(client, club, body):
    r = client.post("/api/admin/products", json=body, headers=club["staff"])
    assert r.status_code == 200, r.text
    return r.json()


def order(client, guest, *lines, comment=None):
    return client.post("/api/me/orders", headers=guest["headers"], json={
        "items": [{"product_id": pid, "qty": qty} for pid, qty in lines], "comment": comment})


# ---------- меню ----------
def test_staff_manages_the_menu_and_guest_sees_only_visible_items(env):
    client, club, pc, guest = env
    cola = add_product(client, club, COLA)
    burger = add_product(client, club, {**BURGER, "sort_order": 10})
    seen = client.get("/api/me/products", headers=guest["headers"]).json()
    assert [p["name"] for p in seen] == ["Бургер", "Кола"]                      # по порядку, потом по номеру
    assert set(seen[0]) == {"id", "name", "category", "price"}

    assert client.patch(f"/api/admin/products/{cola['id']}", json={"active": False}, headers=club["staff"]).json()["active"] is False
    assert [p["name"] for p in client.get("/api/me/products", headers=guest["headers"]).json()] == ["Бургер"]
    assert len(client.get("/api/admin/products", headers=club["staff"]).json()) == 2   # в админке скрытый виден

    r = client.patch(f"/api/admin/products/{burger['id']}", json={"price": 0}, headers=club["staff"])
    assert r.status_code == 422                                                 # после правки запись проверяется целиком
    assert client.patch(f"/api/admin/products/{burger['id']}", json={"club_id": 9}, headers=club["staff"]).status_code == 422


def test_product_in_orders_cannot_be_deleted_only_hidden(env):
    client, club, pc, guest = env
    cola = add_product(client, club, COLA)
    unused = add_product(client, club, BURGER)
    assert order(client, guest, (cola["id"], 1)).status_code == 200
    assert client.delete(f"/api/admin/products/{cola['id']}", headers=club["staff"]).status_code == 409
    assert client.delete(f"/api/admin/products/{unused['id']}", headers=club["staff"]).status_code == 200


# ---------- заказ и деньги ----------
def test_order_is_paid_from_balance_immediately_and_visible_to_staff_with_the_pc(env):
    client, club, pc, guest = env
    cola, burger = add_product(client, club, COLA), add_product(client, club, BURGER)
    r = order(client, guest, (cola["id"], 2), (burger["id"], 1), comment="без лука")
    assert r.status_code == 200, r.text
    body = r.json()
    assert body["total"] == 2 * 80 + 250 and body["status"] == "new" and body["balance"] == 500 - 410
    assert balance_of(client, club, guest["id"]) == 90
    tx = ledger(client, club, guest["id"])[0]
    assert (tx["amount"], tx["kind"]) == (-410, "shop_purchase") and tx["comment"] == f"Заказ №{body['id']}"

    (listed,) = client.get("/api/admin/orders", headers=club["staff"]).json()
    assert listed["pc_name"] == "PC-1" and listed["guest_phone"] == guest["phone"] and listed["comment"] == "без лука"
    assert [(i["name"], i["qty"]) for i in listed["items"]] == [("Кола", 2), ("Бургер", 1)]
    assert client.get("/api/admin/inbox", headers=club["staff"]).json() == {"orders_new": 1, "orders_open": 1, "calls_open": 0}


def test_price_comes_from_the_server_and_duplicates_are_merged(env):
    client, club, pc, guest = env
    cola = add_product(client, club, COLA)
    r = client.post("/api/me/orders", headers=guest["headers"], json={
        "items": [{"product_id": cola["id"], "qty": 1}, {"product_id": cola["id"], "qty": 2, "price": 1}]})
    assert r.status_code == 200 and r.json()["total"] == 240 and len(r.json()["items"]) == 1


def test_not_enough_money_hidden_or_foreign_product_and_bad_input_are_refused(client):
    a = make_club(client, "club-a")
    b = make_club(client, "club-b", phone=PHONE_B)
    pc_a = make_pc(client, a)
    guest = make_guest(client, a, pc_a, balance=100)
    cola = add_product(client, a, COLA)
    foreign = add_product(client, b, {**COLA, "name": "Чужая кола"})
    hidden = add_product(client, a, {**BURGER, "active": False})

    r = order(client, guest, (cola["id"], 2))
    assert r.status_code == 402 and "Не хватает 60 ₽" in r.json()["detail"]
    assert balance_of(client, a, guest["id"]) == 100                           # ничего не списано
    assert order(client, guest, (foreign["id"], 1)).status_code == 404         # товар другого клуба
    assert order(client, guest, (hidden["id"], 1)).status_code == 404
    assert order(client, guest, (9999, 1)).status_code == 404
    assert client.post("/api/me/orders", headers=guest["headers"], json={"items": []}).status_code == 422
    assert order(client, guest, (cola["id"], 0)).status_code == 422
    assert order(client, guest, (cola["id"], 21)).status_code == 422
    assert client.post("/api/me/orders", json={"items": [{"product_id": cola["id"], "qty": 1}]}).status_code == 401
    assert client.get("/api/admin/orders", headers=guest["headers"]).status_code in (401, 403)


def test_guest_cannot_have_more_than_five_open_orders(env):
    client, club, pc, guest = env
    cola = add_product(client, club, {**COLA, "price": 1})
    top_up(client, club, guest["id"], 100)
    for _ in range(5):
        assert order(client, guest, (cola["id"], 1)).status_code == 200
    r = order(client, guest, (cola["id"], 1))
    assert r.status_code == 429
    # после выдачи одного заказа можно заказывать дальше
    first = client.get("/api/admin/orders", headers=club["staff"]).json()[0]
    assert client.post(f"/api/admin/orders/{first['id']}/status", json={"status": "done"}, headers=club["staff"]).status_code == 200
    assert order(client, guest, (cola["id"], 1)).status_code == 200


# ---------- статусы, отмена и возвраты ----------
def test_staff_moves_the_order_through_the_statuses(env):
    client, club, pc, guest = env
    cola = add_product(client, club, COLA)
    oid = order(client, guest, (cola["id"], 1)).json()["id"]
    url = f"/api/admin/orders/{oid}/status"
    assert client.post(url, json={"status": "accepted"}, headers=club["staff"]).json()["status"] == "accepted"
    assert client.post(url, json={"status": "done"}, headers=club["staff"]).json()["status"] == "done"
    assert client.post(url, json={"status": "cancelled"}, headers=club["staff"]).status_code == 409   # выдан: отменять поздно
    assert client.post(url, json={"status": "accepted"}, headers=club["staff"]).status_code == 409
    assert client.post(url, json={"status": "nonsense"}, headers=club["staff"]).status_code == 422
    assert client.get("/api/admin/orders", headers=club["staff"]).json() == []                          # выданные не висят в очереди
    assert len(client.get("/api/admin/orders?status=all", headers=club["staff"]).json()) == 1
    actions = [a["action"] for a in client.get("/api/admin/audit", headers=club["staff"]).json()]
    assert "order_accepted" in actions and "order_done" in actions


def test_staff_cancel_refunds_exactly_once(env):
    client, club, pc, guest = env
    cola = add_product(client, club, COLA)
    oid = order(client, guest, (cola["id"], 3)).json()["id"]
    assert balance_of(client, club, guest["id"]) == 260
    url = f"/api/admin/orders/{oid}/status"
    assert client.post(url, json={"status": "cancelled"}, headers=club["staff"]).status_code == 200
    assert balance_of(client, club, guest["id"]) == 500
    assert client.post(url, json={"status": "cancelled"}, headers=club["staff"]).status_code == 409   # второй раз не вернёт
    assert balance_of(client, club, guest["id"]) == 500
    kinds = [t["kind"] for t in ledger(client, club, guest["id"])]
    assert kinds[:2] == ["shop_refund", "shop_purchase"]


def test_guest_can_cancel_only_until_the_order_is_accepted(env):
    client, club, pc, guest = env
    cola = add_product(client, club, COLA)
    first = order(client, guest, (cola["id"], 1)).json()["id"]
    second = order(client, guest, (cola["id"], 1)).json()["id"]
    done = client.post(f"/api/me/orders/{first}/cancel", headers=guest["headers"])
    assert done.status_code == 200 and done.json()["status"] == "cancelled" and done.json()["balance"] == 500 - 80
    assert client.post(f"/api/me/orders/{first}/cancel", headers=guest["headers"]).status_code == 409   # уже отменён

    client.post(f"/api/admin/orders/{second}/status", json={"status": "accepted"}, headers=club["staff"])
    assert client.post(f"/api/me/orders/{second}/cancel", headers=guest["headers"]).status_code == 409
    assert balance_of(client, club, guest["id"]) == 420


def test_guest_sees_own_orders_and_cannot_touch_someone_elses(env):
    client, club, pc, guest = env
    pc2 = make_pc(client, club, "PC-2")
    other = make_guest(client, club, pc2, phone=GUEST2, balance=300)
    cola = add_product(client, club, COLA)
    mine = order(client, guest, (cola["id"], 1)).json()["id"]
    theirs = order(client, other, (cola["id"], 1)).json()["id"]
    assert [o["id"] for o in client.get("/api/me/orders", headers=guest["headers"]).json()] == [mine]
    assert client.post(f"/api/me/orders/{theirs}/cancel", headers=guest["headers"]).status_code == 404
    assert balance_of(client, club, other["id"]) == 220


def test_orders_and_menu_are_isolated_between_clubs(client):
    a = make_club(client, "club-a")
    b = make_club(client, "club-b", phone=PHONE_B)
    pc_a = make_pc(client, a)
    guest = make_guest(client, a, pc_a, balance=500)
    cola = add_product(client, a, COLA)
    oid = order(client, guest, (cola["id"], 1)).json()["id"]
    assert client.get("/api/admin/orders", headers=b["staff"]).json() == []
    assert client.get("/api/admin/inbox", headers=b["staff"]).json() == {"orders_new": 0, "orders_open": 0, "calls_open": 0}
    assert client.post(f"/api/admin/orders/{oid}/status", json={"status": "cancelled"}, headers=b["staff"]).status_code == 404
    assert client.patch(f"/api/admin/products/{cola['id']}", json={"price": 1}, headers=b["staff"]).status_code == 404
    assert client.delete(f"/api/admin/products/{cola['id']}", headers=b["staff"]).status_code == 404


def test_ledger_always_matches_the_balance_after_orders_and_refunds(env):
    client, club, pc, guest = env
    cola = add_product(client, club, COLA)
    ids = [order(client, guest, (cola["id"], 1)).json()["id"] for _ in range(3)]
    client.post(f"/api/me/orders/{ids[0]}/cancel", headers=guest["headers"])
    client.post(f"/api/admin/orders/{ids[1]}/status", json={"status": "cancelled"}, headers=club["staff"])
    rows = list(reversed(ledger(client, club, guest["id"])))
    total = 0
    for r in rows:
        total += r["amount"]
        assert r["balance_after"] == total
    assert balance_of(client, club, guest["id"]) == total == 500 - 80


# ---------- вызов администратора ----------
def test_call_admin_creates_one_open_call_per_pc_and_staff_closes_it(env):
    client, club, pc, guest = env
    first = client.post("/api/me/calls", json={"text": "  не запускается игра "}, headers=guest["headers"])
    assert first.status_code == 200 and first.json()["already_open"] is False and first.json()["text"] == "не запускается игра"
    again = client.post("/api/me/calls", json={}, headers=guest["headers"])
    assert again.json()["already_open"] is True and again.json()["id"] == first.json()["id"]
    assert client.get("/api/me/calls/current", headers=guest["headers"]).json()["open"]["id"] == first.json()["id"]

    (call,) = client.get("/api/admin/calls", headers=club["staff"]).json()
    assert call["pc_name"] == "PC-1" and call["guest_phone"] == guest["phone"]
    assert client.get("/api/admin/inbox", headers=club["staff"]).json()["calls_open"] == 1

    done = client.post(f"/api/admin/calls/{call['id']}/done", headers=club["staff"])
    assert done.status_code == 200 and done.json()["status"] == "done"
    assert client.get("/api/me/calls/current", headers=guest["headers"]).json() == {"open": None}
    assert client.get("/api/admin/calls", headers=club["staff"]).json() == []
    assert len(client.get("/api/admin/calls?open_only=false", headers=club["staff"]).json()) == 1
    # после закрытия можно вызвать снова
    assert client.post("/api/me/calls", json={}, headers=guest["headers"]).json()["already_open"] is False


def test_calls_are_isolated_and_need_a_logged_in_guest(client):
    a = make_club(client, "club-a")
    b = make_club(client, "club-b", phone=PHONE_B)
    pc_a = make_pc(client, a)
    guest = make_guest(client, a, pc_a)
    cid = client.post("/api/me/calls", json={}, headers=guest["headers"]).json()["id"]
    assert client.get("/api/admin/calls", headers=b["staff"]).json() == []
    assert client.post(f"/api/admin/calls/{cid}/done", headers=b["staff"]).status_code == 404
    assert client.post("/api/me/calls", json={}, headers=pc_a["headers"]).status_code == 401      # только ключ ПК: гость не вошёл


# ---------- отчёт ----------
def test_shift_report_counts_bar_sales_without_cancelled_orders(env):
    client, club, pc, guest = env
    cola = add_product(client, club, COLA)
    keep = order(client, guest, (cola["id"], 2)).json()["id"]
    gone = order(client, guest, (cola["id"], 1)).json()["id"]
    client.post(f"/api/me/orders/{gone}/cancel", headers=guest["headers"])
    since = (datetime.now(timezone.utc) - timedelta(days=1)).replace(tzinfo=None).isoformat()
    summary = client.get(f"/api/admin/report/summary?since={since}", headers=club["staff"]).json()
    assert summary["shop_orders"] == 1 and summary["shop_revenue"] == 160
    assert keep


def test_starter_menu_seeds_only_an_empty_bar_and_passes_validation(env):
    from app.db import SessionLocal
    from app.schemas import ProductIn
    from app.services import shop
    from app.timeutil import utcnow
    client, club, pc, guest = env
    with SessionLocal() as db:
        assert shop.seed_starter_products(db, club["id"], utcnow()) == len(shop.STARTER_PRODUCTS)
        db.commit()
        assert shop.seed_starter_products(db, club["id"], utcnow()) == 0
        db.commit()
    seen = client.get("/api/admin/products", headers=club["staff"]).json()
    assert len(seen) == len(shop.STARTER_PRODUCTS)
    for p in seen:
        ProductIn.model_validate({k: p[k] for k in ("name", "category", "price", "sort_order", "active")})
