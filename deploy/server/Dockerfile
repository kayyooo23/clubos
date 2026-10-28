# Образ сервера ClubOS. Собирается из корня репозитория:  docker build -f deploy/server/Dockerfile -t clubos-server .
# (или командой docker compose из этой папки, см. README.md)
FROM python:3.14-slim

ENV PYTHONUNBUFFERED=1 PYTHONDONTWRITEBYTECODE=1

# клиент PostgreSQL нужен для резервных копий (pg_dump, pg_restore, psql): его версия должна быть не ниже версии базы
RUN apt-get update && apt-get install -y --no-install-recommends postgresql-client && rm -rf /var/lib/apt/lists/*

WORKDIR /srv/clubos
COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt

COPY alembic.ini .
COPY alembic ./alembic
COPY app ./app
COPY scripts ./scripts

# данные живут в томе /data (версии клиента, резервные копии), сам контейнер можно пересоздавать
RUN useradd --system --uid 10001 clubos && mkdir -p /data/releases /data/backups && chown -R clubos /data
USER clubos
ENV CLUB_ENV=prod CLUB_RELEASES_DIR=/data/releases CLUB_BACKUP_DIR=/data/backups

EXPOSE 8000
HEALTHCHECK --interval=30s --timeout=5s --start-period=60s --retries=3 \
  CMD python -c "import urllib.request; urllib.request.urlopen('http://127.0.0.1:8000/api/health', timeout=4)"

# миграции применяются при каждом старте (безопасно повторять); --proxy-headers: настоящий адрес ПК берётся из заголовка прокси, а не адрес самого прокси
CMD ["sh", "-c", "python -m alembic upgrade head && exec uvicorn app.main:app --host 0.0.0.0 --port 8000 --proxy-headers --forwarded-allow-ips='*'"]
