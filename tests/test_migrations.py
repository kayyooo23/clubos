"""Миграции Alembic должны давать ровно ту же схему, что описана в models.py, и на SQLite (разработка), и на PostgreSQL (боевая база).
Проверки на PostgreSQL идут, только если задан CLUB_TEST_DB_URL (см. scripts/pg_dev.ps1): для них создаётся отдельная чистая база с суффиксом _migr."""
import pytest
from alembic import command
from alembic.autogenerate import compare_metadata
from alembic.config import Config
from alembic.migration import MigrationContext
from sqlalchemy import create_engine, inspect, text
from sqlalchemy.engine import make_url

from app import models  # noqa: F401
from app.db import Base, engine as app_engine

from .conftest import ON_POSTGRES


def _config(url: str) -> Config:
    cfg = Config("alembic.ini")  # тесты запускаются из корня проекта
    cfg.set_main_option("sqlalchemy.url", url)
    return cfg


def _fresh_postgres_url() -> str:
    """Чистая база рядом с тестовой (clubos_test -> clubos_test_migr): миграции должны пройти с нуля."""
    base = app_engine.url
    name = f"{base.database}_migr"
    admin = create_engine(base.set(database="postgres"), isolation_level="AUTOCOMMIT")
    with admin.connect() as conn:
        conn.execute(text(f'DROP DATABASE IF EXISTS "{name}" WITH (FORCE)'))
        conn.execute(text(f'CREATE DATABASE "{name}"'))
    admin.dispose()
    return base.set(database=name).render_as_string(hide_password=False)


@pytest.fixture(params=["sqlite", pytest.param("postgres", marks=pytest.mark.skipif(not ON_POSTGRES, reason="нужен CLUB_TEST_DB_URL с PostgreSQL"))])
def migration_db(request, tmp_path):
    """(настройки Alembic, адрес чистой базы) для каждой СУБД."""
    url = f"sqlite:///{(tmp_path / 'm.db').as_posix()}" if request.param == "sqlite" else _fresh_postgres_url()
    yield _config(url), url, request.param
    if request.param == "postgres":
        create_engine(url).dispose()


def test_migrations_match_models(migration_db):
    cfg, url, _ = migration_db
    command.upgrade(cfg, "head")
    engine = create_engine(url)
    with engine.connect() as conn:
        diff = compare_metadata(MigrationContext.configure(conn), Base.metadata)
    assert diff == [], f"models.py и миграции разошлись, создай новую миграцию: {diff}"
    assert {"clubs", "pcs", "tariffs", "accounts", "logins", "sessions", "zones"} <= set(inspect(engine).get_table_names())


def test_partial_unique_indexes_keep_their_where_clause(migration_db):
    """Пересоздание таблицы в SQLite умеет терять WHERE у частичного индекса: тогда на ПК была бы возможна
    всего одна сессия за всё время. Проверяем, что условие на месте после всех миграций (на PostgreSQL тоже)."""
    cfg, url, backend = migration_db
    command.upgrade(cfg, "head")
    with create_engine(url).connect() as conn:
        if backend == "sqlite":
            rows = conn.exec_driver_sql("select name, sql from sqlite_master where type = 'index'").all()
        else:
            rows = conn.exec_driver_sql("select indexname, indexdef from pg_indexes where schemaname = 'public'").all()
    found = {name: sql for name, sql in rows if name.startswith("uq_") and sql}
    expected_partial = {"uq_sessions_one_active_per_pc", "uq_sessions_one_active_per_account", "uq_logins_one_active_per_pc", "uq_calls_one_open_per_pc"}
    assert expected_partial <= set(found), found
    for name in expected_partial:
        needle = "'open'" if name == "uq_calls_one_open_per_pc" else "ended_at IS NULL"      # PostgreSQL пишет условие по-своему: ((status)::text = 'open'::text)
        assert "WHERE" in found[name] and needle in found[name], (name, found[name])      # условие на месте: на PostgreSQL оно может быть в скобках


def test_migrations_can_go_down_and_up(migration_db):
    cfg, url, _ = migration_db
    command.upgrade(cfg, "head")
    command.downgrade(cfg, "base")
    assert set(inspect(create_engine(url)).get_table_names()) <= {"alembic_version"}
    command.upgrade(cfg, "head")


def test_migration_turns_the_existing_map_zones_into_templates_and_links_the_pcs(migration_db):
    """У клуба уже были залы на карте (просто названия): миграция делает из них шаблоны по одному на название и привязывает ПК. Тарифы остаются общими."""
    cfg, url, _ = migration_db
    command.upgrade(cfg, "0014")
    engine = create_engine(url)
    with engine.begin() as conn:
        for club in (1, 2):
            conn.execute(text(f"insert into clubs (id, slug, name, utc_offset, status, created_at) values ({club}, 'c{club}', 'Клуб {club}', 3, 'active', '2026-01-01 00:00:00')"))
        pcs = [(1, 1, "PC-1", "Общий зал"), (2, 1, "PC-2", "Общий зал"), (3, 1, "VIP-1", "VIP"), (4, 2, "PC-1", "Общий зал")]
        for pid, club, name, zone in pcs:
            conn.execute(text("insert into pcs (id, club_id, name, key_hash, zone) values (:id, :club, :name, :hash, :zone)"),
                         {"id": pid, "club": club, "name": name, "hash": f"hash{pid}", "zone": zone})
        conn.execute(text("insert into tariffs (id, club_id, name, kind, price, active) values (1, 1, 'Час', 'hourly', 120, true)"))

    command.upgrade(cfg, "head")
    with engine.connect() as conn:
        zones = conn.execute(text("select club_id, name from zones order by club_id, name")).all()
        linked = conn.execute(text("select p.name, z.name, z.club_id = p.club_id from pcs p join zones z on z.id = p.zone_id order by p.id")).all()
        tariff_zone = conn.execute(text("select zone_id from tariffs where id = 1")).scalar()
    assert [tuple(z) for z in zones] == [(1, "VIP"), (1, "Общий зал"), (2, "Общий зал")]            # один шаблон на название в клубе, клубы не смешиваются
    assert [tuple(r) for r in linked] == [("PC-1", "Общий зал", True), ("PC-2", "Общий зал", True), ("VIP-1", "VIP", True), ("PC-1", "Общий зал", True)]
    assert tariff_zone is None                                                                      # прежние тарифы общие: клуб работает как раньше


def test_migration_lets_the_wallet_journal_take_shop_kinds_and_downgrade_keeps_the_rows(tmp_path):
    """В тестах база строится из моделей, а настоящая база идёт через миграции: проверяем именно их (только SQLite: там таблица пересоздаётся)."""
    from sqlalchemy.exc import IntegrityError
    url = f"sqlite:///{(tmp_path / 'm.db').as_posix()}"
    cfg = _config(url)
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
