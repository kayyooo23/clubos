"""Восстановление базы ClubOS из копии. ВАЖНО: рабочую базу восстанавливайте, только остановив сервер; для проверки копий (раз в месяц) разворачивайте их в отдельную базу.
    SQLite:      python -m scripts.restore backups/club-20261013-030000.sqlite3.gz --target sqlite:///./restored.db
    PostgreSQL:  createdb clubos_restored; python -m scripts.restore backups/club-20261013-030000.pgdump --target postgresql+psycopg://user:pass@host/clubos_restored
После восстановления выполните  python -m alembic upgrade head  (если копия снята со старой версии сервера, схема обновится) и проверьте число строк в таблицах ниже."""
import argparse
import sys
from pathlib import Path

from app.services import backups


def main(argv: list[str] | None = None) -> int:
    p = argparse.ArgumentParser(description="Восстановление базы ClubOS из копии")
    p.add_argument("file", help="файл копии (club-*.sqlite3.gz или club-*.pgdump)")
    p.add_argument("--target", required=True, help="адрес базы, куда разворачивать (обязателен: по умолчанию ничего не восстанавливается, чтобы не затереть рабочую базу)")
    p.add_argument("--force", action="store_true", help="SQLite: заменить уже существующий файл")
    args = p.parse_args(argv)
    try:
        counts = backups.restore(Path(args.file), args.target, args.force)
    except (backups.BackupError, OSError) as e:
        print(f"Не восстановлено: {e}", file=sys.stderr)
        return 1
    print("Восстановлено. Строк в главных таблицах:", ", ".join(f"{k}: {v}" for k, v in counts.items()))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
