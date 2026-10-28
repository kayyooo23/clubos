from sqlalchemy import create_engine, event
from sqlalchemy.orm import sessionmaker, DeclarativeBase

from . import config

_is_sqlite = config.DB_URL.startswith("sqlite")
if _is_sqlite:
    engine = create_engine(config.DB_URL, connect_args={"check_same_thread": False})
else:
    # PostgreSQL: запросы сервера идут в пуле нитей, поэтому соединений нужно больше стандартных 5+10;
    # pool_pre_ping: оборванное соединение (перезапуск базы, сетевой сбой) заменяется, а не роняет запрос
    engine = create_engine(config.DB_URL, pool_size=config.DB_POOL_SIZE, max_overflow=config.DB_POOL_SIZE * 2, pool_pre_ping=True, pool_recycle=1800)
SessionLocal = sessionmaker(bind=engine, autoflush=False, expire_on_commit=False)


if _is_sqlite:
    @event.listens_for(engine, "connect")
    def _enable_foreign_keys(dbapi_conn, _):
        dbapi_conn.execute("PRAGMA foreign_keys=ON")  # в SQLite внешние ключи по умолчанию НЕ проверяются


class Base(DeclarativeBase):
    pass


def get_db():
    db = SessionLocal()
    try:
        yield db
    finally:
        db.close()
