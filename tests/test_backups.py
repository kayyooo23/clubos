"""Резервные копии: создание и проверка, ротация, восстановление, сценарий отказа. Данные берутся из настоящей тестовой базы (SQLite или PostgreSQL, как запущены тесты)."""
import gzip
import shutil
import sqlite3
from datetime import datetime, timedelta, timezone
from pathlib import Path

import pytest
from sqlalchemy import func, select

from app import config
from app.db import SessionLocal
from app.models import Account, Club, WalletTransaction
from app.services import backups
from scripts import backup as backup_cli
from scripts import restore as restore_cli

from .conftest import ON_POSTGRES
from .helpers import GUEST1, GUEST2, make_club, make_guest, make_pc, top_up

NOW = datetime(2026, 10, 13, 12, 0, 0)


def counts():
    with SessionLocal() as db:
        return {"clubs": db.scalar(select(func.count(Club.id))), "accounts": db.scalar(select(func.count(Account.id))),
                "wallet_transactions": db.scalar(select(func.count(WalletTransaction.id)))}


@pytest.fixture()
def populated(client):
    club = make_club(client)
    pc = make_pc(client, club)
    guest = make_guest(client, club, pc, phone=GUEST1)
    make_guest(client, club, pc, phone=GUEST2)
    top_up(client, club, guest["id"], 500)
    return client, club


def name(days_ago=0, hours=12, kind="sqlite"):
    return backups.new_name(NOW - timedelta(days=days_ago) - timedelta(hours=12 - hours), kind)


# ---------- ротация ----------
def test_names_round_trip_and_foreign_files_are_ignored():
    n = backups.new_name(NOW, "sqlite")
    assert n == "club-20261013-120000.sqlite3.gz" and backups.parse_name(n) == NOW
    assert backups.new_name(NOW, "postgres") == "club-20261013-120000.pgdump"
    for bad in ("club-20261013.sqlite3.gz", "club-20261340-120000.sqlite3.gz", "notes.txt", "club-20261013-120000.zip", "backup-status.json"):
        assert backups.parse_name(bad) is None


def test_retention_keeps_fresh_daily_then_weekly_then_monthly_and_always_the_newest():
    names = [name(d, h) for d in range(0, 400) for h in (3, 15)]                       # две копии в сутки больше года
    keep = backups.select_to_keep(names, NOW, daily=14, weekly=8, monthly=12)
    ages = sorted(((NOW - backups.parse_name(n)).days for n in keep))
    assert name(0, 15) in keep                                                          # самая свежая
    assert len([n for n in keep if (NOW - backups.parse_name(n)).days <= 14]) == 15    # по одной на каждые из последних суток (с учётом границы)
    assert len(keep) < 60 and max(ages) <= 31 * 12                                       # не копится без границ и ничего старше года
    assert name(100, 3) not in keep or name(100, 15) not in keep                        # за один день старше двух недель не лежат обе копии
    one_per_month = {(backups.parse_name(n).year, backups.parse_name(n).month) for n in keep if (NOW - backups.parse_name(n)).days > 56}
    assert len(one_per_month) >= 8                                                       # зато по копии за месяц остаётся


def test_retention_never_touches_foreign_files_and_keeps_the_only_copy(tmp_path):
    (tmp_path / "notes.txt").write_text("мои заметки")
    (tmp_path / name(500)).write_bytes(b"x")                                            # единственная копия, хоть и старая: остаётся
    assert backups.apply_retention(tmp_path, NOW) == []
    assert (tmp_path / "notes.txt").exists() and (tmp_path / name(500)).exists()
    (tmp_path / name(0)).write_bytes(b"x")
    assert backups.apply_retention(tmp_path, NOW) == [name(500)]


# ---------- создание, проверка, восстановление ----------
@pytest.mark.skipif(ON_POSTGRES, reason="сценарий SQLite")
def test_sqlite_backup_is_checked_and_restores_with_the_same_data(populated, tmp_path):
    before = counts()
    assert before["accounts"] >= 3 and before["wallet_transactions"] >= 1
    result = backups.make_backup(config.DB_URL, tmp_path / "b", NOW)
    assert result.file.name == "club-20261013-120000.sqlite3.gz" and result.size > 0 and result.kind == "sqlite"
    assert not list((tmp_path / "b").glob("*.part"))                                    # временных хвостов нет
    target = tmp_path / "restored.db"
    got = backups.restore(result.file, f"sqlite:///{target}")
    assert got == before                                                                # в восстановленной базе столько же строк
    with pytest.raises(backups.BackupError, match="уже есть"):
        backups.restore(result.file, f"sqlite:///{target}")                             # рабочий файл по ошибке не затирается
    assert backups.restore(result.file, f"sqlite:///{target}", force=True) == before


@pytest.mark.skipif(ON_POSTGRES, reason="сценарий SQLite")
def test_a_broken_or_incomplete_copy_is_not_accepted(populated, tmp_path):
    bad = tmp_path / "club-20261013-120000.sqlite3.gz"
    bad.write_bytes("это не gzip".encode())
    with pytest.raises(backups.BackupError, match="не открывается"):
        backups.verify(bad)
    empty = tmp_path / "x.db"
    con = sqlite3.connect(empty)
    con.execute("create table clubs(id integer)")
    con.commit()
    con.close()
    with gzip.open(bad, "wb") as f:
        f.write(empty.read_bytes())
    with pytest.raises(backups.BackupError, match="нет таблиц"):
        backups.verify(bad)                                                              # открывается, но главных таблиц нет: копия негодна
    with pytest.raises(backups.BackupError, match="не найден"):
        backups.make_backup(f"sqlite:///{tmp_path / 'missing.db'}", tmp_path / "b", NOW)
    assert not list((tmp_path / "b").glob("club-*"))                                    # неудачная попытка не оставляет ни копии, ни хвоста


# ---------- команды ----------
@pytest.mark.skipif(ON_POSTGRES, reason="сценарий SQLite")
def test_the_backup_command_makes_a_copy_prunes_old_ones_and_reports_in_a_status_file(populated, tmp_path, capsys):
    dest = tmp_path / "b"
    dest.mkdir()
    long_ago = datetime.now(timezone.utc).replace(tzinfo=None) - timedelta(days=400)
    (dest / backups.new_name(long_ago, "sqlite")).write_bytes(b"old")                                  # очень старая копия уйдёт
    (dest / "readme.txt").write_text("чужой файл")
    copy_ok, copy_bad = tmp_path / "nas", tmp_path / "file-instead-of-folder"
    copy_bad.write_text("это файл, а не папка")
    assert backup_cli.main(["--dest", str(dest), "--db-url", config.DB_URL, "--copy-to", str(copy_ok)]) == 0
    assert len(list(copy_ok.glob("club-*.sqlite3.gz"))) == 1                            # вторая копия лежит там, где просили
    status = backups.read_status(dest)
    assert status["ok"] is True and status["file"].startswith("club-") and len(status["removed"]) == 1
    assert (dest / "readme.txt").exists()
    capsys.readouterr()
    assert backup_cli.main(["--dest", str(dest), "--db-url", config.DB_URL, "--copy-to", str(copy_bad)]) == 1      # копия в дополнительное место не удалась: тревога, хотя основная есть
    assert backups.read_status(dest)["ok"] is False and "не удалось скопировать" in backups.read_status(dest)["message"]
    assert "не скопировано" in capsys.readouterr().err


def test_the_backup_command_fails_loudly_when_the_database_is_missing(tmp_path, capsys):
    dest = tmp_path / "b"
    assert backup_cli.main(["--dest", str(dest), "--db-url", f"sqlite:///{tmp_path / 'nope.db'}"]) == 1
    assert backups.read_status(dest)["ok"] is False
    assert "не сделана" in capsys.readouterr().err


@pytest.mark.skipif(ON_POSTGRES, reason="сценарий SQLite")
def test_the_restore_command_needs_an_explicit_target_and_refuses_to_overwrite(populated, tmp_path, capsys):
    result = backups.make_backup(config.DB_URL, tmp_path / "b", NOW)
    target = tmp_path / "r.db"
    assert restore_cli.main([str(result.file), "--target", f"sqlite:///{target}"]) == 0
    assert "accounts" in capsys.readouterr().out
    assert restore_cli.main([str(result.file), "--target", f"sqlite:///{target}"]) == 1             # файл уже есть
    assert restore_cli.main([str(result.file), "--target", f"sqlite:///{target}", "--force"]) == 0
    assert restore_cli.main([str(result.file), "--target", "postgresql+psycopg://u@h/d"]) == 1      # копия SQLite в PostgreSQL не разворачивается
    with pytest.raises(SystemExit):
        restore_cli.main([str(result.file)])                                                          # без --target ничего не делается


# ---------- PostgreSQL ----------
@pytest.mark.skipif(not ON_POSTGRES, reason="сценарий PostgreSQL")
def test_postgres_backup_restores_into_another_database_with_the_same_data(populated, tmp_path):
    try:
        backups.pg_tool("pg_dump")
    except backups.BackupError:
        pytest.skip("нет pg_dump")
    from sqlalchemy.engine import make_url
    before = counts()
    result = backups.make_backup(config.DB_URL, tmp_path / "b", NOW)
    assert result.file.suffix == ".pgdump" and result.size > 0
    url = make_url(config.DB_URL)
    target_db = "clubos_restore_check"
    args, env = backups._pg_args(url)
    backups._run([backups.pg_tool("psql"), *args, "-d", "postgres", "-c", f"drop database if exists {target_db}"], env)
    backups._run([backups.pg_tool("psql"), *args, "-d", "postgres", "-c", f"create database {target_db}"], env)
    try:
        got = backups.restore(result.file, url.set(database=target_db).render_as_string(hide_password=False))
        assert got == before
    finally:
        backups._run([backups.pg_tool("psql"), *args, "-d", "postgres", "-c", f"drop database if exists {target_db}"], env)
