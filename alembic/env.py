from logging.config import fileConfig

from sqlalchemy import create_engine, pool

from alembic import context

from app import config as app_config
from app.db import Base
from app import models  # noqa: F401  (импорт регистрирует таблицы в Base.metadata)

config = context.config
if config.config_file_name is not None:
    fileConfig(config.config_file_name)

target_metadata = Base.metadata


def _url() -> str:
    # адрес БД берём из настроек приложения (CLUB_DB_URL); тесты могут подставить свой через Config
    return config.get_main_option("sqlalchemy.url") or app_config.DB_URL


def run_migrations_offline() -> None:
    context.configure(url=_url(), target_metadata=target_metadata, literal_binds=True,
                      dialect_opts={"paramstyle": "named"}, render_as_batch=True)
    with context.begin_transaction():
        context.run_migrations()


def run_migrations_online() -> None:
    connectable = create_engine(_url(), poolclass=pool.NullPool)
    with connectable.connect() as connection:
        # render_as_batch: SQLite не умеет ALTER для большинства изменений, batch пересоздаёт таблицу
        context.configure(connection=connection, target_metadata=target_metadata, render_as_batch=True)
        with context.begin_transaction():
            context.run_migrations()


if context.is_offline_mode():
    run_migrations_offline()
else:
    run_migrations_online()
