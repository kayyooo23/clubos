"""Веб-админка: раздача файлов, защитные заголовки, чистота исходников и серверные части (отчёт, поиск, журнал)."""
import re
from datetime import datetime, timedelta, timezone
from pathlib import Path

from sqlalchemy import select

from app import webui
from app.db import SessionLocal
from app.models import Club
from app.services import accounts
from app.timeutil import utcnow

from .helpers import (GUEST1, GUEST2, GUEST3, PASSWORD, PHONE_A, PHONE_B, make_club, make_guest, make_pc, make_tariff,
                      shift_session, staff_login, start_for, top_up)

WEB = Path(webui.WEB_DIR)
PLATFORM_WEB = Path(webui.PLATFORM_DIR)
SOURCES = [p for d in (WEB, PLATFORM_WEB) for p in d.rglob("*") if p.suffix in {".js", ".html", ".css"}]   # админка клуба и панель платформы


# ---------- раздача файлов и заголовки ----------
def test_admin_pages_are_served(client):
    r = client.get("/admin/")
    assert r.status_code == 200 and "text/html" in r.headers["content-type"]
    assert '<script type="module" src="app.js">' in r.text
    assert client.get("/admin").status_code == 200                 # без слэша тоже (редирект на /admin/)
    for path, kind in (("app.js", "javascript"), ("style.css", "css"), ("views/map.js", "javascript"), ("views/topup.js", "javascript"), ("views/pcdialogs.js", "javascript"), ("format.js", "javascript")):
        got = client.get(f"/admin/{path}")
        assert got.status_code == 200 and kind in got.headers["content-type"], path
    assert client.get("/admin/nope.js").status_code == 404
    assert client.get("/", follow_redirects=False).headers["location"] == "/admin/"


def test_static_files_cannot_escape_the_web_folder(client):
    for path in ("/admin/%2e%2e/main.py", "/admin/..%2f..%2fapp/main.py", "/admin/views/../../main.py", "/admin/%5c..%5c..%5cmain.py"):
        r = client.get(path)
        assert r.status_code in (404, 400) and "FastAPI" not in r.text, path


def test_security_headers(client):
    admin = client.get("/admin/")
    csp = admin.headers["content-security-policy"]
    for needle in ("default-src 'none'", "script-src 'self'", "style-src 'self'", "connect-src 'self'", "frame-ancestors 'none'", "base-uri 'none'"):
        assert needle in csp, needle
    assert "unsafe-inline" not in csp and "unsafe-eval" not in csp
    assert admin.headers["x-frame-options"] == "DENY"
    assert admin.headers["x-content-type-options"] == "nosniff"
    assert client.get("/admin/app.js").headers["content-security-policy"] == csp   # и на скриптах, не только на странице
    api = client.get("/api/admin/pcs")
    assert api.headers["cache-control"] == "no-store" and api.headers["x-content-type-options"] == "nosniff"
    assert "content-security-policy" not in client.get("/docs").headers            # /docs (Swagger) работает как работал


# ---------- исходники админки ----------
def test_sources_use_no_dangerous_dom_apis():
    """Админка строит страницу только через текстовые узлы: чужой текст не может стать разметкой или скриптом."""
    forbidden = [r"innerHTML", r"outerHTML", r"insertAdjacentHTML", r"document\.write", r"\beval\s*\(", r"new\s+Function",
                 r"setTimeout\s*\(\s*['\"`]", r"createContextualFragment", r"srcdoc", r"\.setAttribute\(\s*['\"]on", r"javascript:"]
    for path in SOURCES:
        text = path.read_text(encoding="utf-8")
        for pattern in forbidden:
            assert not re.search(pattern, text), f"{path.name}: запрещённый приём {pattern}"


def test_sources_load_nothing_from_outside():
    """Ни CDN, ни внешних шрифтов: CSP это и так запретил бы, а админка должна работать в локальной сети клуба без интернета."""
    for path in SOURCES:
        text = path.read_text(encoding="utf-8")
        assert not re.search(r"https?://", text.replace("http://www.w3.org/2000/svg", "")), f"{path.name}: внешний адрес"
    for page in (WEB / "index.html", PLATFORM_WEB / "index.html"):
        html = page.read_text(encoding="utf-8")
        assert "style=" not in html and "<script>" not in html and "onclick" not in html   # встроенные скрипты и стили CSP запрещает


def test_visible_text_has_no_long_dashes():
    """Длинное и среднее тире в текстах интерфейса выглядят как «машинный» почерк; в видимых строках только обычный дефис.
    Комментарии разработчика проверка пропускает."""
    import re
    for path in SOURCES:
        text = path.read_text(encoding="utf-8")
        if path.suffix == ".js":
            text = re.sub(r"/\*.*?\*/", "", text, flags=re.S)           # блочные комментарии
            text = re.sub(r"(?m)^\s*//.*$", "", text)                   # строки-комментарии
            text = re.sub(r"(?m)\s//[^'\"`\n]*$", "", text)             # хвостовые комментарии
        elif path.suffix == ".css":
            text = re.sub(r"/\*.*?\*/", "", text, flags=re.S)
        elif path.suffix == ".html":
            text = re.sub(r"<!--.*?-->", "", text, flags=re.S)
        assert "—" not in text and "–" not in text, f"{path.name}: длинное/среднее тире в видимом тексте"


def test_every_view_exports_mount():
    for view in ("map", "guests", "sessions", "tariffs", "report", "audit"):
        assert "export function mount(" in (WEB / "views" / f"{view}.js").read_text(encoding="utf-8"), view


# ---------- сервер: поддержка админки ----------
def test_me_returns_the_club_timezone(client):
    club = make_club(client, utc_offset=5)
    me = client.get("/api/admin/auth/me", headers=club["staff"]).json()
    assert me["club"]["utc_offset"] == 5 and me["club"]["name"]


def test_find_guests_by_part_of_the_number(client):
    club = make_club(client)
    pc = make_pc(client, club)
    a, b = make_guest(client, club, pc, GUEST1), make_guest(client, club, make_pc(client, club, "PC-2"), GUEST2)
    find = lambda q, **k: client.get("/api/admin/accounts", params={"q": q, **k}, headers=club["staff"])
    assert [x["id"] for x in find("9031").json()] == [a["id"]]              # кусок номера +79031234567
    assert {x["id"] for x in find("4567").json()} == {a["id"], b["id"]}      # общий хвост двух номеров
    assert [x["id"] for x in find("+7 (903)").json()] == [a["id"]]           # лишние символы не мешают
    assert find("999").json() == []
    assert find("12").status_code == 422 and find("abc").status_code == 422
    assert find("%%%").status_code == 422 and find("_").status_code == 422   # LIKE-символы не работают как шаблон
    assert find("1234", limit=1).json().__len__() == 1


def test_guest_search_never_crosses_clubs(client):
    a, b = make_club(client, "club-a", PHONE_A), make_club(client, "club-b", PHONE_B)
    make_guest(client, a, make_pc(client, a), GUEST1)
    assert client.get("/api/admin/accounts", params={"q": "4567"}, headers=b["staff"]).json() == []


def test_session_history_has_pc_and_guest(client):
    club = make_club(client)
    pc, t = make_pc(client, club, "PC-7"), make_tariff(client, club)
    guest = make_guest(client, club, pc, balance=500)
    start_for(client, club, guest, t)
    row = client.get("/api/admin/sessions", headers=club["staff"]).json()[0]
    assert row["pc_name"] == "PC-7" and row["guest_phone"] == GUEST1
    pcs = client.get("/api/admin/pcs", headers=club["staff"]).json()
    assert pcs[0]["session"]["kind"] == "hourly"          # админка по нему подписывает таймер («денег хватит на» / «до конца тарифа»)


def test_audit_names_the_actor_and_the_pc(client):
    club = make_club(client)
    pc = make_pc(client, club, "PC-3")
    client.post("/api/auth/login", json={"phone": PHONE_A, "password": PASSWORD}, headers=pc["headers"])
    rows = client.get("/api/admin/audit", headers=club["staff"]).json()
    unlock = next(r for r in rows if r["action"] == "staff_unlock")
    assert unlock["actor_phone"] == PHONE_A and unlock["actor_role"] == "staff" and unlock["pc_name"] == "PC-3"
    login = next(r for r in rows if r["action"] == "admin_login")
    assert login["pc_name"] is None


# ---------- сводный отчёт ----------
def second_staff(client, slug, phone):
    with SessionLocal() as db:
        club = db.scalar(select(Club).where(Club.slug == slug))
        accounts.create_account(db, club.id, phone, PASSWORD, "staff", utcnow())
    return {"Authorization": f"Bearer {staff_login(client, slug, phone, PASSWORD)}"}


def build_day(client):
    """Два кассира, три гостя, пакет и почасовая сессия, ручная корректировка."""
    club = make_club(client)
    cashier2 = second_staff(client, "club-a", GUEST3)
    pc1, pc2 = make_pc(client, club, "PC-1"), make_pc(client, club, "PC-2")
    package = make_tariff(client, club, name="3 часа", kind="package", price=250, package_minutes=180)
    hourly = make_tariff(client, club, name="Час", price=100)
    g1, g2 = make_guest(client, club, pc1, GUEST1), make_guest(client, club, pc2, GUEST2)
    top_up(client, club, g1["id"], 300)                                                   # кассир 1: +300
    client.post(f"/api/admin/accounts/{g2['id']}/topup", json={"amount": 200}, headers=cashier2)  # кассир 2: +200
    client.post(f"/api/admin/accounts/{g1['id']}/adjust", json={"amount": -50, "comment": "ошибка"}, headers=club["staff"])
    s1 = start_for(client, club, g1, package).json()
    client.post(f"/api/admin/sessions/{s1['id']}/stop", headers=club["staff"])
    s2 = start_for(client, club, g2, hourly).json()
    shift_session(s2["id"], minutes=30)
    done = client.post(f"/api/admin/sessions/{s2['id']}/stop", headers=club["staff"]).json()
    return club, cashier2, done["cost"]


def summary(client, headers, since="2000-01-01T00:00:00", **params):
    return client.get("/api/admin/report/summary", params={"since": since, **params}, headers=headers)


def test_summary_adds_up(client):
    club, _, hourly_cost = build_day(client)
    data = summary(client, club["staff"]).json()
    assert data["sessions"] == 2
    assert data["revenue"] == 250 + hourly_cost
    assert [t["tariff"] for t in data["by_tariff"]] == ["3 часа", "Час"]                  # по убыванию выручки
    assert data["by_tariff"][0] == {"tariff": "3 часа", "sessions": 1, "revenue": 250}
    assert data["cash_in"] == 500
    assert {s["staff"]: (s["amount"], s["operations"]) for s in data["cash_by_staff"]} == {PHONE_A: (300, 1), GUEST3: (200, 1)}
    assert sum(s["amount"] for s in data["cash_by_staff"]) == data["cash_in"]
    assert data["adjustments"] == -50 and data["adjustments_count"] == 1


def test_summary_respects_the_period(client):
    club, _, _ = build_day(client)
    now = datetime.now(timezone.utc)
    window = summary(client, club["staff"], since=(now - timedelta(days=3)).isoformat(), until=(now - timedelta(days=2)).isoformat())
    empty = window.json()      # окно двухдневной давности: тогда ещё ничего не было
    assert (empty["sessions"], empty["revenue"], empty["cash_in"], empty["adjustments"]) == (0, 0, 0, 0)
    assert empty["by_tariff"] == [] and empty["cash_by_staff"] == []
    yesterday_end = (datetime.now(timezone.utc) - timedelta(days=1)).isoformat()
    assert summary(client, club["staff"], until=yesterday_end).json()["cash_in"] == 0
    assert summary(client, club["staff"], since="2000-01-01T00:00:00+03:00").status_code == 200   # с часовым поясом


def test_summary_rejects_backwards_period(client):
    club = make_club(client)
    r = summary(client, club["staff"], since="2026-10-02T00:00:00", until="2026-10-01T00:00:00")
    assert r.status_code == 422


def test_summary_is_per_club_and_needs_staff(client):
    club, _, _ = build_day(client)
    other = make_club(client, "club-b", PHONE_B)
    data = summary(client, other["staff"]).json()
    assert (data["sessions"], data["revenue"], data["cash_in"], data["adjustments"]) == (0, 0, 0, 0)
    assert client.get("/api/admin/report/summary", params={"since": "2000-01-01T00:00:00"}).status_code == 401
    pc = make_pc(client, club, "PC-9")
    assert client.get("/api/admin/report/summary", params={"since": "2000-01-01T00:00:00"}, headers=pc["headers"]).status_code == 401


def test_platform_panel_is_served_with_the_same_protection_as_the_admin(client):
    page = client.get("/platform/")
    assert page.status_code == 200 and '<script type="module" src="platform.js">' in page.text
    assert client.get("/platform").status_code == 200
    assert client.get("/platform/platform.js").status_code == 200
    assert page.headers["content-security-policy"] == client.get("/admin/").headers["content-security-policy"]
    assert page.headers["x-frame-options"] == "DENY" and page.headers["cache-control"] == "no-cache"
    assert client.get("/platform/%2e%2e/main.py").status_code in (404, 400)
    # сама страница ничего не раскрывает: все данные платформы идут только по API и только с ключом платформы
    assert client.get("/api/platform/ads").status_code in (401, 403)


def test_computers_tab_is_merged_into_the_club_map():
    """Отдельной вкладки «Компьютеры» нет: всё делается на карте клуба, а старая ссылка #/pcs ведёт на неё."""
    app = (WEB / "app.js").read_text(encoding="utf-8")
    assert "views/pcs.js" not in app and "title: 'Компьютеры'" not in app
    assert "raw === 'pcs' ? 'map'" in app
    assert not (WEB / "views" / "pcs.js").exists()
    club_map = (WEB / "views" / "map.js").read_text(encoding="utf-8")
    for needed in ("startSessionDialog", "addPcDialog", "rotateKeyFlow", "topupByPc"):   # то, что раньше было только на вкладке «Компьютеры»
        assert needed in club_map, needed
    assert "'Пополнить баланс'" in club_map
