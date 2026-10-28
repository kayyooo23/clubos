"""Резервная копия базы ClubOS (SQLite или PostgreSQL, по CLUB_DB_URL). Запускать по расписанию (раз в сутки, лучше чаще), например:
    python -m scripts.backup --dest /var/backups/clubos --copy-to /mnt/nas/clubos
Что делает: снимает копию, проверяет её (открывается, главные таблицы на месте), кладёт под именем club-ГГГГММДД-ЧЧММСС.*, копирует в дополнительные папки,
убирает лишние старые копии (14 суток, 8 недель, 12 месяцев) и пишет backup-status.json: его показывает панель платформы. Код возврата 0: всё хорошо, 1: нет."""
import argparse
import sys
from datetime import datetime, timezone
from pathlib import Path

from app import config
from app.services import backups


def main(argv: list[str] | None = None) -> int:
    p = argparse.ArgumentParser(description="Резервная копия базы ClubOS")
    p.add_argument("--dest", default=config.BACKUP_DIR, help="папка копий (по умолчанию CLUB_BACKUP_DIR или ./backups)")
    p.add_argument("--db-url", default=config.DB_URL, help="адрес базы (по умолчанию CLUB_DB_URL)")
    p.add_argument("--copy-to", action="append", default=[], help="ещё одна папка для копии (можно несколько раз): сетевой или внешний диск")
    p.add_argument("--keep-daily", type=int, default=14)
    p.add_argument("--keep-weekly", type=int, default=8)
    p.add_argument("--keep-monthly", type=int, default=12)
    args = p.parse_args(argv)

    dest = Path(args.dest)
    now = datetime.now(timezone.utc).replace(tzinfo=None)
    try:
        result = backups.make_backup(args.db_url, dest, now, [Path(c) for c in args.copy_to])
    except (backups.BackupError, OSError) as e:
        backups.write_status(dest, now, False, str(e))
        print(f"Копия не сделана: {e}", file=sys.stderr)
        return 1
    removed = backups.apply_retention(dest, now, args.keep_daily, args.keep_weekly, args.keep_monthly)
    failed_copies = [c for c in result.copies if not c["ok"]]
    message = "Копия сделана и проверена" + (f"; не удалось скопировать в {len(failed_copies)} доп. папок" if failed_copies else "")
    backups.write_status(dest, now, not failed_copies, message, result, removed)
    print(f"{message}: {result.file} ({result.size / 1024 / 1024:.1f} МБ)" + (f", убрано старых: {len(removed)}" if removed else ""))
    for c in failed_copies:
        print(f"  не скопировано в {c['path']}: {c.get('error')}", file=sys.stderr)
    return 1 if failed_copies else 0


if __name__ == "__main__":
    raise SystemExit(main())
