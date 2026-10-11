"""Миграции Alembic должны давать ровно ту же схему, что описана в models.py."""
from alembic import command
from alembic.autogenerate import compare_metadata
from alembic.config import Config
from alembic.migration import MigrationContext
from sqlalchemy import create_engine, inspect

from app import models  # noqa: F401
from app.db import Base


def _config(db_path) -> tuple[Config, str]:
    url = f"sqlite:///{db_path.as_posix()}"
    cfg = Config("alembic.ini")  # тесты запускаются из корня проекта
    cfg.set_main_option("sqlalchemy.url", url)
    return cfg, url


def test_migrations_match_models(tmp_path):
    cfg, url = _config(tmp_path / "m.db")
    command.upgrade(cfg, "head")
    engine = create_engine(url)
    with engine.connect() as conn:
        diff = compare_metadata(MigrationContext.configure(conn), Base.metadata)
    assert diff == [], f"models.py и миграции разошлись, создай новую миграцию: {diff}"
    assert {"clubs", "pcs", "tariffs", "accounts", "logins", "sessions"} <= set(inspect(engine).get_table_names())


def test_partial_unique_indexes_keep_their_where_clause(tmp_path):
    """Пересоздание таблицы в SQLite умеет терять WHERE у частичного индекса — тогда на ПК была бы возможна
    всего одна сессия за всё время. Проверяем, что условие на месте после всех миграций."""
    cfg, url = _config(tmp_path / "m.db")
    command.upgrade(cfg, "head")
    with create_engine(url).connect() as conn:
        rows = conn.exec_driver_sql("select name, sql from sqlite_master where name like 'uq\\_%' escape '\\'").all()
    found = {name: sql for name, sql in rows}
    assert set(found) == {"uq_sessions_one_active_per_pc", "uq_sessions_one_active_per_account", "uq_logins_one_active_per_pc",
                          "uq_calls_one_open_per_pc"}
    for name, sql in found.items():
        assert ("WHERE status = 'open'" if name == "uq_calls_one_open_per_pc" else "WHERE ended_at IS NULL") in sql, (name, sql)


def test_migrations_can_go_down_and_up(tmp_path):
    cfg, url = _config(tmp_path / "m.db")
    command.upgrade(cfg, "head")
    command.downgrade(cfg, "base")
    assert set(inspect(create_engine(url)).get_table_names()) <= {"alembic_version"}
    command.upgrade(cfg, "head")


def test_migration_lets_the_wallet_journal_take_shop_kinds_and_downgrade_keeps_the_rows(tmp_path):
    """В тестах база строится из моделей, а настоящая база идёт через миграции: проверяем именно их."""
    import pytest
    from sqlalchemy.exc import IntegrityError
    cfg, url = _config(tmp_path / "m.db")
    command.upgrade(cfg, "head")
    insert = ("insert into wallet_transactions (club_id, account_id, amount, balance_after, kind, created_at) "
              "values (1, 1, -50, 10, '{kind}', '2026-01-01 12:00:00')")
    engine = create_engine(url)
    with engine.begin() as conn:
        conn.exec_driver_sql(insert.format(kind="shop_purchase"))
        conn.exec_driver_sql(insert.format(kind="shop_refund"))
    with pytest.raises(IntegrityError):
        with engine.begin() as conn:
            conn.exec_driver_sql(insert.format(kind="made_up_kind"))

    command.downgrade(cfg, "0004")
    with engine.connect() as conn:
        kinds = [r[0] for r in conn.exec_driver_sql("select kind from wallet_transactions").all()]
    assert kinds == ["adjustment", "adjustment"]           # записи не потеряны, а переведены в корректировки
    with pytest.raises(IntegrityError):
        with engine.begin() as conn:
            conn.exec_driver_sql(insert.format(kind="shop_purchase"))

