from contextlib import asynccontextmanager

from fastapi import FastAPI, Request
from fastapi.responses import JSONResponse
from sqlalchemy import inspect

from .db import engine
from .errors import ServiceError
from . import webui
from .routers import admin, admin_auth, agent, auth, me, platform


@asynccontextmanager
async def lifespan(_: FastAPI):
    if not inspect(engine).has_table("clubs"):
        raise RuntimeError("База не создана. Выполни: python -m alembic upgrade head")
    yield


app = FastAPI(title="ClubOS server", lifespan=lifespan)


@app.exception_handler(ServiceError)
async def service_error_handler(_: Request, exc: ServiceError):
    return JSONResponse({"detail": exc.detail}, status_code=exc.status)


app.include_router(platform.router)
app.include_router(admin_auth.router)
app.include_router(admin.router)
app.include_router(agent.router)
app.include_router(auth.router)
app.include_router(me.router)
webui.install(app)
