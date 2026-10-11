from datetime import datetime

from sqlalchemy.exc import IntegrityError
from sqlalchemy.orm import Session as DB

from ..errors import ServiceError, commit_or_409
from ..models import Account, Club
from ..security import hash_password


def create_club_with_owner(db: DB, slug: str, name: str, utc_offset: int, owner_phone: str,
                           owner_password: str, now: datetime) -> Club:
    """Клуб + его первый сотрудник создаются одной транзакцией (нет клуба без хозяина и наоборот)."""
    club = Club(slug=slug, name=name, utc_offset=utc_offset)
    db.add(club)
    try:
        db.flush()  # получаем club.id; нарушение уникальности slug всплывёт здесь
    except IntegrityError:
        db.rollback()
        raise ServiceError(409, "Клуб с таким кодом уже есть")
    db.add(Account(club_id=club.id, phone=owner_phone, password_hash=hash_password(owner_password),
                   role="staff", phone_verified_at=now))
    commit_or_409(db, "Не удалось создать клуб")
    return club
