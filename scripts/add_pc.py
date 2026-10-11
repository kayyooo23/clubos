"""Создаёт ПК в клубе (или выдаёт новый ключ существующему) — то же, что кнопка «Добавить ПК» в веб-админке.

    python -m scripts.add_pc <код клуба> <название ПК>
    python -m scripts.add_pc demo Laptop --write-agent-json agent.json --server http://192.168.1.80:8000

Ключ ПК показывается один раз. С --write-agent-json он записывается прямо в файл настроек клиента и в консоль не печатается.
"""
import argparse
import json
import sys

from sqlalchemy import select

from app.db import SessionLocal
from app.models import Club, PC
from app.security import hash_secret, new_secret


def main() -> None:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("club", help="код клуба (slug)")
    ap.add_argument("name", help="название ПК")
    ap.add_argument("--write-agent-json", metavar="ФАЙЛ", help="записать адрес сервера и ключ в файл настроек клиента")
    ap.add_argument("--server", help="адрес сервера для файла настроек, например http://192.168.1.80:8000")
    args = ap.parse_args()
    if args.write_agent_json and not args.server:
        ap.error("для --write-agent-json нужен --server")

    key = new_secret()
    with SessionLocal() as db:
        club = db.scalar(select(Club).where(Club.slug == args.club))
        if not club:
            sys.exit(f"Клуб с кодом «{args.club}» не найден")
        pc = db.scalar(select(PC).where(PC.club_id == club.id, PC.name == args.name))
        if pc:
            pc.key_hash = hash_secret(key)
            what = "ключ обновлён (старый перестал работать)"
        else:
            db.add(PC(club_id=club.id, name=args.name, key_hash=hash_secret(key)))
            what = "ПК создан"
        db.commit()

    print(f"{args.name}: {what}")
    if args.write_agent_json:
        with open(args.write_agent_json, "w", encoding="utf-8") as f:
            json.dump({"server": args.server.rstrip("/"), "pc_key": key}, f, ensure_ascii=False, indent=2)
        print(f"Настройки клиента записаны в {args.write_agent_json} (ключ внутри, в консоль не печатается).")
    else:
        print(f"КЛЮЧ (показывается один раз): {key}")


if __name__ == "__main__":
    main()
