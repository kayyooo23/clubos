import os
import re

os.environ["CLUB_DB_URL"] = "sqlite:///./test_club.db"  # до импорта app: db.py читает адрес при импорте
os.environ.pop("CLUB_ENV", None)
os.environ["CLUB_STEAM_AUTOCOVERS"] = "0"  # тесты не ходят в Steam сами; фоновую подгрузку проверяет test_covers_autofill, включая её явно

import pytest
from fastapi.testclient import TestClient

from argon2 import PasswordHasher

from app import security
from app.db import Base, engine
from app.main import app
from app.services.sms import get_sms

# В тестах хешируем пароли дёшево (иначе сотни регистраций тянутся минутами). В проде остаются стойкие параметры по умолчанию.
security._ph = PasswordHasher(time_cost=1, memory_cost=1024, parallelism=1)


class RecordingSms:
    """Вместо настоящих SMS запоминает всё отправленное, чтобы тест мог достать код."""

    def __init__(self):
        self.sent: list[tuple[str, str]] = []

    def send(self, phone: str, text: str) -> None:
        self.sent.append((phone, text))

    def count(self, phone: str | None = None) -> int:
        return len([1 for p, _ in self.sent if phone is None or p == phone])

    def last_code(self, phone: str) -> str:
        for p, text in reversed(self.sent):
            if p == phone:
                return re.search(r"\b(\d{6})\b", text).group(1)
        raise AssertionError(f"на {phone} SMS не отправлялось")


@pytest.fixture()
def client():
    Base.metadata.drop_all(engine)
    Base.metadata.create_all(engine)
    sms = RecordingSms()
    app.dependency_overrides[get_sms] = lambda: sms
    c = TestClient(app)
    c.sms = sms
    yield c
    app.dependency_overrides.clear()
