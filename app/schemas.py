import base64
import binascii
import re
from datetime import datetime
from typing import Annotated, Literal

from pydantic import AfterValidator, BaseModel, ConfigDict, Field, field_validator, model_validator

from . import config
from .phone import normalize_phone

Phone = Annotated[str, AfterValidator(normalize_phone)]  # любой формат на входе -> +79123456789


class PCIn(BaseModel):
    name: str = Field(min_length=1, max_length=50)


class NoticeIn(BaseModel):
    """Сообщение администратора гостю на экран ПК. Длину и пустоту проверяет сервис (понятный ответ по-русски); здесь только предел размера запроса."""
    text: str = Field(max_length=2000)


MAP_COLUMNS, MAP_ROWS = 30, 20   # размер сетки карты клуба


class PCPatch(BaseModel):
    """Правка ПК сотрудником: имя, зал, клетка на карте, заметка, «на обслуживании». Пришло только то, что меняем."""
    model_config = ConfigDict(extra="forbid")
    name: str | None = Field(default=None, min_length=1, max_length=50)
    zone: str | None = Field(default=None, min_length=1, max_length=40)
    pos_x: int | None = Field(default=None, ge=0, lt=MAP_COLUMNS)
    pos_y: int | None = Field(default=None, ge=0, lt=MAP_ROWS)
    note: str | None = Field(default=None, max_length=200)
    maintenance: bool | None = None

    @field_validator("name", "zone", "note")
    @classmethod
    def strip_text(cls, v: str | None) -> str | None:
        return v.strip() if isinstance(v, str) else v


class LayoutItem(BaseModel):
    id: int
    zone: str = Field(min_length=1, max_length=40)
    pos_x: int | None = Field(default=None, ge=0, lt=MAP_COLUMNS)
    pos_y: int | None = Field(default=None, ge=0, lt=MAP_ROWS)


class LayoutIn(BaseModel):
    """Расстановка всех ПК за один раз (после перетаскивания на карте): сохраняется целиком или не сохраняется вовсе."""
    items: list[LayoutItem] = Field(min_length=1, max_length=300)


class DiskIn(BaseModel):
    name: str = Field(max_length=16)
    total_gb: int = Field(ge=0, le=1_000_000)
    free_gb: int = Field(ge=0, le=1_000_000)


class HardwareIn(BaseModel):
    """Что служба на ПК рассказывает о железе. Поля необязательные: что не удалось определить, остаётся пустым."""
    cpu: str | None = Field(default=None, max_length=120)
    ram_gb: int | None = Field(default=None, ge=0, le=100_000)
    gpu: str | None = Field(default=None, max_length=120)
    os: str | None = Field(default=None, max_length=100)
    ip: str | None = Field(default=None, max_length=45)
    mac: str | None = Field(default=None, pattern=r"^([0-9A-Fa-f]{2}[:-]){5}[0-9A-Fa-f]{2}$")
    disks: list[DiskIn] = Field(default_factory=list, max_length=8)


_CONTROL = re.compile(r"[\x00-\x1f\x7f]+")


def _one_line(v: str | None) -> str | None:
    """Переводы строк и служебные символы заменяем пробелом: название и подпись баннера идут в одну строку."""
    if v is None:
        return None
    v = " ".join(_CONTROL.sub(" ", v).split())
    return v or None


MAX_PROGRAMS = 800
MAX_ICON_B64 = 40_000                  # иконка одной программы: PNG до 30 КБ, в base64 около 40 тысяч знаков
ICON_BUDGET_B64 = 4_000_000            # на весь список: больше иконок сервер не принимает (записи остаются без иконки)


def _clean_icon(raw) -> str | None:
    """PNG от службы в base64 (иконка программы). Негодную (не PNG, огромную, странного размера) отбрасываем: программа остаётся без иконки."""
    if not isinstance(raw, str) or not raw or len(raw) > MAX_ICON_B64:
        return None
    try:
        data = base64.b64decode(raw, validate=True)
    except (ValueError, binascii.Error):
        return None
    if not data.startswith(b"\x89PNG\r\n\x1a\n") or len(data) < 33:
        return None
    width, height = int.from_bytes(data[16:20], "big"), int.from_bytes(data[20:24], "big")
    return raw if 48 <= width <= 256 and 48 <= height <= 256 else None


class ProgramsIn(BaseModel):
    """Программы, которые служба нашла на ПК. Негодные записи (путь не к exe, чужой диск, «..») отбрасываются, остальные принимаются."""
    programs: list[dict] = Field(default_factory=list)

    @field_validator("programs", mode="before")
    @classmethod
    def keep_valid(cls, items):
        if not isinstance(items, list):
            raise ValueError("programs: нужен список")
        out, seen, budget = [], set(), ICON_BUDGET_B64
        for item in items:
            if not isinstance(item, dict) or not isinstance(item.get("path"), str) or not isinstance(item.get("name"), str):
                continue
            name = (_one_line(item["name"]) or "")[:120]
            path = item["path"].strip()
            if not name or len(path) > 260:
                continue
            try:
                check_launch_target("exe", path)
            except ValueError:
                continue
            if path.lower() in seen:
                continue
            seen.add(path.lower())
            entry = {"name": name, "path": path}
            icon = _clean_icon(item.get("icon"))
            if icon and budget >= len(icon):
                entry["icon"] = icon
                budget -= len(icon)
            out.append(entry)
            if len(out) >= MAX_PROGRAMS:
                break
        return out


class AdIn(BaseModel):
    """Объявление партнёра. Окно показа (starts_at, ends_at) задаётся в UTC."""
    text: str = Field(min_length=1, max_length=200)
    advertiser: str | None = Field(default=None, max_length=120)
    erid: str | None = Field(default=None, max_length=80)
    active: bool = True
    starts_at: datetime | None = None
    ends_at: datetime | None = None
    sort_order: int = Field(default=100, ge=0, le=10000)
    duration_seconds: int = Field(default=10, ge=3, le=120)

    @field_validator("text", "advertiser", "erid")
    @classmethod
    def clean(cls, v: str | None) -> str | None:
        return _one_line(v)

    @field_validator("text")
    @classmethod
    def not_empty(cls, v: str | None) -> str:
        if not v:
            raise ValueError("не может быть пустым")
        return v

    @model_validator(mode="after")
    def check_window(self):
        if self.starts_at and self.ends_at and self.ends_at <= self.starts_at:
            raise ValueError("конец показа должен быть позже начала")
        return self


class AdPatch(BaseModel):
    """Частичная правка объявления; после слияния запись проверяется целиком как AdIn."""
    model_config = ConfigDict(extra="forbid")
    text: str | None = None
    advertiser: str | None = None
    erid: str | None = None
    active: bool | None = None
    starts_at: datetime | None = None
    ends_at: datetime | None = None
    sort_order: int | None = None
    duration_seconds: int | None = None


class ClubAdsIn(BaseModel):
    """Платформа задаёт клубу: включена ли реклама и какая доля дохода достаётся клубу."""
    enabled: bool | None = None
    club_share: int | None = Field(default=None, ge=0, le=100)


class ClubAdsToggleIn(BaseModel):
    enabled: bool


class BookingPatch(BaseModel):
    """Правка брони сотрудником: пришло только то, что меняем. Пустая строка в name или comment стирает значение;
    сдвиг времени (starts_at, minutes) и смена ПК проверяются так же, как при создании."""
    model_config = ConfigDict(extra="forbid")
    pc_id: int | None = None
    phone: Phone | None = None
    name: str | None = Field(default=None, max_length=80)
    comment: str | None = Field(default=None, max_length=200)
    starts_at: datetime | None = None
    minutes: int | None = Field(default=None, ge=15, le=720)

    @field_validator("name", "comment")
    @classmethod
    def clean(cls, v: str | None) -> str | None:
        return v.strip() if v is not None else None


class BookingIn(BaseModel):
    """Новая бронь: ПК, телефон гостя, начало (UTC) и длительность в минутах."""
    pc_id: int
    phone: Phone
    name: str | None = Field(default=None, max_length=80)
    comment: str | None = Field(default=None, max_length=200)
    starts_at: datetime
    minutes: int = Field(ge=15, le=720)

    @field_validator("name", "comment")
    @classmethod
    def clean(cls, v: str | None) -> str | None:
        v = (v or "").strip()
        return v or None


class TariffIn(BaseModel):
    name: str = Field(min_length=1, max_length=80)
    kind: Literal["hourly", "package", "night"]
    price: int = Field(gt=0)
    package_minutes: int | None = Field(default=None, gt=0)
    night_start: int | None = Field(default=None, ge=0, le=23)
    night_end: int | None = Field(default=None, ge=0, le=23)

    @model_validator(mode="after")
    def check(self):
        if self.kind == "package" and not self.package_minutes:
            raise ValueError("для пакета нужен package_minutes")
        if self.kind == "night" and (self.night_start is None or self.night_end is None):
            raise ValueError("для ночного тарифа нужны night_start и night_end")
        if self.kind == "night" and self.night_start == self.night_end:
            raise ValueError("night_start и night_end не должны совпадать")
        return self


_STEAM_ID = re.compile(r"^[1-9]\d{0,9}$")
_EXE_PATH = re.compile(r"^[A-Za-z]:\\[^<>:\"/|?*\x00-\x1f]+\.exe$", re.IGNORECASE)


def check_launch_target(kind: str, target: str) -> str:
    """Что можно запускать из меню. Путь только вида C:\\папка\\игра.exe (без UNC и «..»), для Steam только число."""
    if kind == "steam":
        if not _STEAM_ID.match(target):
            raise ValueError("номер игры в Steam: только цифры, например 570")
    elif kind == "exe":
        if re.match(r"^[A-Za-z]:\\", target) and not target.lower().endswith(".exe"):
            # частая ошибка: вставили путь к ПАПКЕ с программой, а нужен файл запуска
            raise ValueError(r"нужен путь к файлу программы (он оканчивается на .exe), а не к папке: например C:\Games\Game\game.exe")
        parts = target.split("\\")
        if not _EXE_PATH.match(target) or any(p in ("", ".", "..") for p in parts[1:]):
            raise ValueError(r"путь к exe: полный, вида C:\Games\Game\game.exe")
    return target


class AppIn(BaseModel):
    """Игра или программа в меню гостя."""
    title: str = Field(min_length=1, max_length=80)
    tab: Literal["games", "apps"] = "games"
    category: str = Field(default="Другое", min_length=1, max_length=40)
    age_rating: Literal[0, 6, 12, 16, 18] | None = None
    launch_kind: Literal["steam", "exe"]
    launch_target: str = Field(min_length=1, max_length=260)
    sort_order: int = Field(default=100, ge=0, le=10000)
    active: bool = True

    @field_validator("title", "category", "launch_target")
    @classmethod
    def strip_text(cls, v: str) -> str:
        v = v.strip()
        if not v:
            raise ValueError("не может быть пустым")
        return v

    @model_validator(mode="after")
    def check_target(self):
        check_launch_target(self.launch_kind, self.launch_target)
        return self


class AppPatch(BaseModel):
    """Частичная правка: пришло только то, что меняем; после слияния запись проверяется целиком как AppIn."""
    model_config = ConfigDict(extra="forbid")
    title: str | None = None
    tab: str | None = None
    category: str | None = None
    age_rating: int | None = None
    launch_kind: str | None = None
    launch_target: str | None = None
    sort_order: int | None = None
    active: bool | None = None


class CopyAppsIn(BaseModel):
    from_club_id: int
    replace: bool = False  # True: сначала убрать каталог получателя, иначе копируем сверху


class ProductIn(BaseModel):
    """Товар в баре клуба."""
    name: str = Field(min_length=1, max_length=80)
    category: str = Field(default="Напитки", min_length=1, max_length=40)
    price: int = Field(gt=0, le=config.MAX_TOPUP)
    sort_order: int = Field(default=100, ge=0, le=10000)
    active: bool = True

    @field_validator("name", "category")
    @classmethod
    def strip_text(cls, v: str) -> str:
        v = v.strip()
        if not v:
            raise ValueError("не может быть пустым")
        return v


class ProductPatch(BaseModel):
    """Частичная правка товара; после слияния запись проверяется целиком как ProductIn."""
    model_config = ConfigDict(extra="forbid")
    name: str | None = None
    category: str | None = None
    price: int | None = None
    sort_order: int | None = None
    active: bool | None = None


class OrderItemIn(BaseModel):
    product_id: int
    qty: int = Field(ge=1, le=20)


class OrderIn(BaseModel):
    """Заказ гостя: что и сколько. Цена берётся с сервера, не из запроса."""
    items: list[OrderItemIn] = Field(min_length=1, max_length=20)
    comment: str | None = Field(default=None, max_length=200)

    @field_validator("comment")
    @classmethod
    def clean_comment(cls, v: str | None) -> str | None:
        v = (v or "").strip()
        return v or None


class OrderStatusIn(BaseModel):
    status: Literal["accepted", "done", "cancelled"]


class CallIn(BaseModel):
    """Вызов администратора; текст необязателен («нужна помощь с игрой»)."""
    text: str | None = Field(default=None, max_length=100)

    @field_validator("text")
    @classmethod
    def clean_text(cls, v: str | None) -> str | None:
        v = (v or "").strip()
        return v or None


class SessionIn(BaseModel):
    """Старт сессии сотрудником за конкретного гостя (с его баланса)."""
    pc_id: int
    tariff_id: int
    account_id: int


class GuestSessionIn(BaseModel):
    """Старт сессии самим гостем: ПК берётся из ключа ПК, гость — из токена."""
    tariff_id: int


Code = Annotated[str, Field(pattern=r"^\d{6}$")]


def _guest_password(v: str) -> str:
    if len(v) < config.GUEST_PASSWORD_MIN:
        raise ValueError(f"пароль: минимум {config.GUEST_PASSWORD_MIN} символов")
    return v


GuestPassword = Annotated[str, Field(max_length=128), AfterValidator(_guest_password)]


class RequestCodeIn(BaseModel):
    phone: Phone


class RegisterConfirmIn(BaseModel):
    phone: Phone
    code: Code
    password: GuestPassword
    accept_terms: bool  # согласие на обработку персональных данных (152-ФЗ)

    @field_validator("accept_terms")
    @classmethod
    def must_accept(cls, v: bool) -> bool:
        if not v:
            raise ValueError("нужно согласие на обработку персональных данных")
        return v


class PCLoginIn(BaseModel):
    phone: Phone
    password: str = Field(max_length=128)


class ResetConfirmIn(BaseModel):
    phone: Phone
    code: Code
    new_password: GuestPassword


class MoneyIn(BaseModel):
    amount: int = Field(gt=0, le=config.MAX_TOPUP)
    comment: str | None = Field(default=None, max_length=200)


class AdjustIn(BaseModel):
    amount: int = Field(ge=-config.MAX_TOPUP, le=config.MAX_TOPUP)
    comment: str = Field(min_length=1, max_length=200)  # корректировка без объяснения недопустима

    @field_validator("amount")
    @classmethod
    def not_zero(cls, v: int) -> int:
        if v == 0:
            raise ValueError("сумма не может быть нулевой")
        return v


class StaffPasswordResetIn(BaseModel):
    phone: Phone
    new_password: str = Field(max_length=128)

    @field_validator("new_password")
    @classmethod
    def long_enough(cls, v: str) -> str:
        if len(v) < config.STAFF_PASSWORD_MIN:
            raise ValueError(f"пароль сотрудника: минимум {config.STAFF_PASSWORD_MIN} символов")
        return v


class ClubIn(BaseModel):
    slug: str = Field(pattern=r"^[a-z0-9][a-z0-9-]{2,39}$")
    name: str = Field(min_length=1, max_length=100)
    utc_offset: int = Field(default=3, ge=-12, le=14)
    owner_phone: Phone
    owner_password: str = Field(max_length=128)

    @field_validator("owner_password")
    @classmethod
    def long_enough(cls, v: str) -> str:
        if len(v) < config.STAFF_PASSWORD_MIN:
            raise ValueError(f"пароль сотрудника: минимум {config.STAFF_PASSWORD_MIN} символов")
        return v


class ClubStatusIn(BaseModel):
    status: Literal["active", "suspended"]


class StaffLoginIn(BaseModel):
    club: str = Field(max_length=40)  # slug клуба; это только «куда входим», пароль проверяется внутри клуба
    phone: Phone
    password: str = Field(max_length=128)
