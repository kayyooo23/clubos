"""Имитация клиента на ПК гостя — чтобы проверять весь сервер без настоящего клиента на Windows.

  python fake_agent.py setup <ключ ПК> [адрес сервера]   запомнить ключ ПК (файл .fake_agent.json); сервер по умолчанию localhost:8000
  python fake_agent.py watch                              показывать, что видит ПК: заблокирован / играет / рабочий стол
  python fake_agent.py register <телефон>                 регистрация: код из SMS (в dev он печатается в окне сервера) + пароль
  python fake_agent.py login <телефон>                    вход (гость → кабинет; сотрудник → рабочий стол)
  python fake_agent.py me | tariffs | transactions        кабинет: баланс, тарифы, история
  python fake_agent.py start <id тарифа> | stop           старт / остановка своей сессии
  python fake_agent.py logout                             выход
  python fake_agent.py reset <телефон>                    сброс пароля по SMS

Ключ ПК показывается один раз при создании ПК (POST /api/admin/pcs) или печатается `python -m scripts.seed_dev`.
Пополнить баланс гостя можно в /docs: POST /api/admin/accounts/{id}/topup."""
import json, pathlib, sys, time
from getpass import getpass

import httpx

STATE = pathlib.Path(".fake_agent.json")
sys.stdout.reconfigure(encoding="utf-8", errors="replace")


def load() -> dict:
    return json.loads(STATE.read_text(encoding="utf-8")) if STATE.exists() else {}


def save(data: dict) -> None:
    STATE.write_text(json.dumps(data, ensure_ascii=False, indent=2), encoding="utf-8")


def call(method: str, path: str, body: dict | None = None, auth: bool = True) -> dict:
    cfg = load()
    if "key" not in cfg:
        sys.exit("Сначала: python fake_agent.py setup <ключ ПК>")
    headers = {"X-PC-Key": cfg["key"]}
    if auth and cfg.get("token"):
        headers["Authorization"] = f"Bearer {cfg['token']}"
    r = httpx.request(method, cfg["server"] + path, json=body, headers=headers, timeout=10)
    data = r.json() if r.content else {}
    if r.status_code >= 400:
        print(f"[ОШИБКА] {r.status_code}: {data.get('detail', data)}")
        sys.exit(1)
    return data


def describe(state: dict) -> str:
    acc, s = state.get("account"), state.get("session")
    if state["mode"] == "staff_desktop":
        return "[РАБОЧИЙ СТОЛ] вошёл сотрудник"
    if state["mode"] == "guest_session":
        left = f"осталось {s['seconds_left'] // 60} мин {s['seconds_left'] % 60} с" if s["seconds_left"] is not None else "почасовой"
        return f"[ИГРАЕТ] {s['tariff']}, {left}, баланс {acc['balance']} ₽"
    cabinet = f", кабинет: {acc['phone_masked']}, баланс {acc['balance']} ₽" if acc else ""
    return "[ЗАБЛОКИРОВАН]" + cabinet + (f" ({state['message']})" if state.get("message") else "")


def watch() -> None:
    last = None
    while True:
        try:
            cfg = load()
            r = httpx.get(cfg["server"] + "/api/agent/state", headers={"X-PC-Key": cfg["key"]}, timeout=10)
            text = describe(r.json()) if r.status_code == 200 else f"[ОШИБКА] сервер ответил {r.status_code}: {r.text}"
            if text != last:
                print(text, flush=True)
                last = text
        except Exception as e:
            print("нет связи с сервером:", e, flush=True)
        time.sleep(2)


def take_token(data: dict) -> None:
    cfg = load()
    cfg["token"] = data["token"]
    save(cfg)
    acc = data["account"]
    print(f"Вход выполнен: {acc.get('phone_masked', '')}, роль {acc['role']}, баланс {acc.get('balance', '—')} ₽")


def main() -> None:
    args = sys.argv[1:] or ["watch"]
    cmd, rest = args[0], args[1:]
    if cmd == "setup":
        if not rest:
            sys.exit("Укажи ключ ПК: python fake_agent.py setup <ключ ПК> [адрес сервера]")
        save({"key": rest[0], "server": (rest[1] if len(rest) > 1 else "http://localhost:8000").rstrip("/")})
        print("Сохранено в .fake_agent.json")
    elif cmd == "watch":
        watch()
    elif cmd == "register":
        phone = rest[0]
        call("POST", "/api/auth/register/request-code", {"phone": phone}, auth=False)
        print("Код отправлен (в dev смотри окно, где запущен сервер).")
        code = input("Код из SMS: ").strip()
        password = getpass("Придумай пароль (минимум 8 символов): ")
        agree = input("Согласен на обработку персональных данных? [y/N]: ").lower() == "y"
        take_token(call("POST", "/api/auth/register/confirm",
                        {"phone": phone, "code": code, "password": password, "accept_terms": agree}, auth=False))
    elif cmd == "login":
        take_token(call("POST", "/api/auth/login", {"phone": rest[0], "password": getpass("Пароль: ")}, auth=False))
    elif cmd == "me":
        print(json.dumps(call("GET", "/api/me"), ensure_ascii=False, indent=2))
    elif cmd == "tariffs":
        for t in call("GET", "/api/agent/tariffs", auth=False):
            print(f"{t['id']:>3}  {t['name']:<20} {t['kind']:<8} {t['price']} ₽")
    elif cmd == "transactions":
        for t in call("GET", "/api/me/transactions"):
            print(f"{t['created_at']}  {t['amount']:+6} ₽  → {t['balance_after']} ₽  ({t['kind']})")
    elif cmd == "start":
        print(json.dumps(call("POST", "/api/me/sessions", {"tariff_id": int(rest[0])}), ensure_ascii=False, indent=2))
    elif cmd == "stop":
        print(json.dumps(call("POST", "/api/me/sessions/stop"), ensure_ascii=False, indent=2))
    elif cmd == "logout":
        call("POST", "/api/auth/logout")
        cfg = load()
        cfg.pop("token", None)
        save(cfg)
        print("Вышли.")
    elif cmd == "reset":
        phone = rest[0]
        call("POST", "/api/auth/password-reset/request-code", {"phone": phone}, auth=False)
        print("Если номер зарегистрирован, код отправлен (в dev смотри окно сервера).")
        code = input("Код из SMS: ").strip()
        call("POST", "/api/auth/password-reset/confirm",
             {"phone": phone, "code": code, "new_password": getpass("Новый пароль: ")}, auth=False)
        print("Пароль изменён. Теперь: python fake_agent.py login", phone)
    else:
        sys.exit(__doc__)


if __name__ == "__main__":
    main()
