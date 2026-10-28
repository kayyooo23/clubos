import os
import re

# До импорта app: db.py читает адрес при импорте. По умолчанию тесты идут на SQLite, а с CLUB_TEST_DB_URL (например,
# postgresql+psycopg://postgres@localhost:5433/clubos_test, см. scripts/pg_dev.ps1) на PostgreSQL: так проверяется то же самое на боевой базе.
os.environ["CLUB_DB_URL"] = os.environ.get("CLUB_TEST_DB_URL", "sqlite:///./test_club.db")
os.environ.pop("CLUB_ENV", None)
import tempfile
_RELEASES_TMP = tempfile.mkdtemp(prefix="clubos-test-releases-")   # пакеты версий клиента: у тестов своя временная папка, настоящая не трогается
os.environ["CLUB_RELEASES_DIR"] = _RELEASES_TMP
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


ON_POSTGRES = engine.dialect.name == "postgresql"


@pytest.fixture(scope="session", autouse=True)
def _only_a_test_database():
    """Каждый тест пересоздаёт все таблицы. На внешней базе это опасно: имя базы должно оканчиваться на _test, иначе тесты не стартуют."""
    if engine.dialect.name != "sqlite" and not (engine.url.database or "").endswith("_test"):
        pytest.exit(f"CLUB_TEST_DB_URL указывает на базу «{engine.url.database}»: тесты пересоздают в ней все таблицы. Нужна база с именем на _test.", returncode=2)


@pytest.fixture(scope="session", autouse=True)
def _drop_release_files():
    yield
    import shutil
    shutil.rmtree(_RELEASES_TMP, ignore_errors=True)


@pytest.fixture(autouse=True)
def _empty_release_dir():
    """Базу каждый тест создаёт заново, а с ней и номера версий клиента: файлы пакетов прошлого теста не должны попадаться."""
    for f in os.scandir(_RELEASES_TMP):
        os.unlink(f.path)


@pytest.fixture(autouse=True)
def _fresh_rate_limits():
    """Счётчики неудачных попыток живут в памяти процесса: у каждого теста свои, иначе один тест мог бы заблокировать другой."""
    from app.services import ratelimit
    ratelimit.enroll_failures.reset()


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
