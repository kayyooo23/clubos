"""Демо-данные для ручной проверки через /docs. ТОЛЬКО для разработки.
Запуск (из корня проекта):  python -m scripts.seed_dev
Можно запускать повторно: клуб не дублируется, а ключ ПК-1 выдаётся новый (старый перестаёт работать)."""
from sqlalchemy import select

from app import config
from app.db import SessionLocal
from app.models import Club, PC, Tariff
from app.security import hash_secret, new_secret
from app.services import catalog, clubs, shop
from app.timeutil import utcnow

SLUG = "demo"
OWNER_PHONE = "+79123456789"
OWNER_PASSWORD = "demo-password-123"


def main() -> None:
    if config.ENV == "prod":
        raise SystemExit("seed_dev нельзя запускать в prod")
    with SessionLocal() as db:
        club = db.scalar(select(Club).where(Club.slug == SLUG))
        if not club:
            club = clubs.create_club_with_owner(db, SLUG, "Demo Club", 3, OWNER_PHONE, OWNER_PASSWORD, utcnow())
            db.add_all([
                Tariff(club_id=club.id, name="Час", kind="hourly", price=120),
                Tariff(club_id=club.id, name="3 часа", kind="package", price=250, package_minutes=180),
                Tariff(club_id=club.id, name="Ночь", kind="night", price=400, night_start=23, night_end=8),
            ])
            db.commit()
        catalog.seed_starter_catalog(db, club.id, utcnow())  # меню гостя: популярные игры (в непустой каталог не пишет)
        shop.seed_starter_products(db, club.id, utcnow())    # бар: ходовые напитки и еда (в непустое меню не пишет)
        db.commit()
        key = new_secret()
        pc = db.scalar(select(PC).where(PC.club_id == club.id, PC.name == "PC-1"))
        if pc:
            pc.key_hash = hash_secret(key)
        else:
            db.add(PC(club_id=club.id, name="PC-1", key_hash=hash_secret(key)))
        db.commit()

    print("Демо-клуб готов.")
    print(f"  Вход в админку (POST /api/admin/auth/login):  club={SLUG}  phone={OWNER_PHONE}  password={OWNER_PASSWORD}")
    print(f"  Ключ ПК-1 (заголовок X-PC-Key):               {key}")
    print(f"  Имитация клиента:  python fake_agent.py setup {key}   затем   python fake_agent.py watch")


if __name__ == "__main__":
    main()
