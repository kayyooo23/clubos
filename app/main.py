from contextlib import asynccontextmanager

from fastapi import Depends, FastAPI, Request
from fastapi.responses import JSONResponse
from sqlalchemy import inspect
from sqlalchemy.orm import Session as DB

from .db import engine, get_db
from .errors import ServiceError
from . import webui
from .routers import admin, admin_auth, agent, auth, me, platform
from .services import ops


@asynccontextmanager
async def lifespan(_: FastAPI):
    if not inspect(engine).has_table("clubs"):
        raise RuntimeError("База не создана. Выполни: python -m alembic upgrade head")
    yield


app = FastAPI(title="ClubOS server", lifespan=lifespan)


@app.exception_handler(ServiceError)
async def service_error_handler(_: Request, exc: ServiceError):
    body = {"detail": exc.detail, **({"code": exc.code} if exc.code else {})}
    return JSONResponse(body, status_code=exc.status)


@app.get("/api/health", tags=["сервер"])
def health(db: DB = Depends(get_db)):
    """Жив ли сервер: для проверок хостинга, Docker и внешнего мониторинга (без входа). 200: база отвечает и схема соответствует коду; 503: нет. Цифр о клубах здесь нет."""
    ok, body = ops.health(db)
    return JSONResponse(body, status_code=200 if ok else 503)


app.include_router(platform.router)
app.include_router(admin_auth.router)
app.include_router(admin.router)
app.include_router(agent.router)
app.include_router(auth.router)
app.include_router(me.router)
webui.install(app)
