"""Кладёт стартовый набор игр, программ и меню бара в клуб (только в то, что у клуба пока пустое).
Запуск (из корня проекта):  python -m scripts.seed_catalog <код клуба>
Потом всё можно поправить в админке, раздел «Игры». Другому клубу тот же список копируется через
POST /api/platform/clubs/{id}/copy-apps."""
import sys

from sqlalchemy import select

from app.db import SessionLocal
from app.models import Club
from app.services import catalog, shop
from app.timeutil import utcnow


def main() -> None:
    if len(sys.argv) != 2:
        raise SystemExit("Использование: python -m scripts.seed_catalog <код клуба>")
    with SessionLocal() as db:
        club = db.scalar(select(Club).where(Club.slug == sys.argv[1]))
        if not club:
            raise SystemExit(f"Клуб «{sys.argv[1]}» не найден")
        now = utcnow()
        added = catalog.seed_starter_catalog(db, club.id, now)
        products = shop.seed_starter_products(db, club.id, now)
        db.commit()
    print(f"Игры и программы: {'добавлено ' + str(added) if added else 'уже есть, ничего не добавлено'}")
    print(f"Бар: {'добавлено товаров ' + str(products) if products else 'уже есть, ничего не добавлено'}")


if __name__ == "__main__":
    main()
