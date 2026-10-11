"""Веб-админка: статические файлы из app/web/admin по адресу /admin и защитные заголовки.

Админка — это обычные файлы (HTML, CSS, JavaScript без сборки и без внешних библиотек), которые ходят в тот же API
(/api/admin/...). Строгая политика CSP разрешает скрипты и стили только с этого же адреса: даже если когда-нибудь в страницу
попадёт чужой текст, он не сможет выполнить код, подключить внешний скрипт или отправить данные на чужой адрес.
"""
from pathlib import Path

from fastapi import FastAPI, Request
from fastapi.responses import RedirectResponse
from starlette.staticfiles import StaticFiles

WEB_DIR = Path(__file__).parent / "web" / "admin"
PLATFORM_DIR = Path(__file__).parent / "web" / "platform"   # панель владельца платформы: реклама, клубы, отчёт

CSP = ("default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self' data:; font-src 'self'; "
       "connect-src 'self'; base-uri 'none'; form-action 'self'; frame-ancestors 'none'")


def install(app: FastAPI) -> None:
    app.mount("/admin", StaticFiles(directory=WEB_DIR, html=True), name="admin")
    app.mount("/platform", StaticFiles(directory=PLATFORM_DIR, html=True), name="platform")

    @app.get("/", include_in_schema=False)
    def root():
        return RedirectResponse("/admin/")

    @app.middleware("http")
    async def security_headers(request: Request, call_next):
        response = await call_next(request)
        path = request.url.path
        response.headers.setdefault("X-Content-Type-Options", "nosniff")
        response.headers.setdefault("Referrer-Policy", "no-referrer")
        if path in ("/admin", "/platform") or path.startswith(("/admin/", "/platform/")):
            response.headers["Content-Security-Policy"] = CSP
            response.headers["X-Frame-Options"] = "DENY"
            response.headers["Cache-Control"] = "no-cache"
        elif path.startswith("/api"):
            response.headers["Cache-Control"] = "no-store"  # токены, балансы: ничего не кэшируем
        return response
