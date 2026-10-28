from sqlalchemy.exc import IntegrityError
from sqlalchemy.orm import Session as DB


class ServiceError(Exception):
    """Ошибка бизнес-логики. main.py превращает её в JSON-ответ {"detail": ...} с нужным кодом."""

    def __init__(self, status: int, detail: str, code: str | None = None):
        super().__init__(detail)
        self.status = status
        self.detail = detail
        self.code = code          # машинное имя ошибки для клиента на ПК («machine_mismatch»): по тексту решать нельзя


def commit_or_409(db: DB, detail: str) -> None:
    """commit, где нарушение уникальности (гонка двух запросов) = понятный 409, а не 500."""
    try:
        db.commit()
    except IntegrityError:
        db.rollback()
        raise ServiceError(409, detail)
