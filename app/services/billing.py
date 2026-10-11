"""Вся логика денег и времени тут, отдельно от API — так её легко тестировать.
Часовой пояс у каждого клуба свой, поэтому utc_offset (часы от UTC) передаётся параметром."""
import math
from datetime import datetime, timedelta

from .. import config
from ..models import Session, Tariff


def to_local(dt: datetime, utc_offset: int) -> datetime:
    return dt + timedelta(hours=utc_offset)


def in_night_window(t: Tariff, now: datetime, utc_offset: int) -> bool:
    h = to_local(now, utc_offset).hour
    s, e = t.night_start, t.night_end
    return (s <= h < e) if s < e else (h >= s or h < e)  # окно через полночь: 23 -> 8


def night_end_time(t: Tariff, now: datetime, utc_offset: int) -> datetime:
    """Ближайший момент night_end (в UTC) после now."""
    local = to_local(now, utc_offset)
    end = local.replace(hour=t.night_end, minute=0, second=0, microsecond=0)
    if end <= local:
        end += timedelta(days=1)
    return end - timedelta(hours=utc_offset)


def hourly_limit_end(t: Tariff, started_at: datetime, balance: int) -> datetime:
    """Когда у почасовой сессии кончатся деньги: на balance рублей хватает balance*60/price минут от старта.
    Пересчитывается при каждом пополнении во время игры, поэтому гость может пополнить баланс и играть дальше."""
    return started_at + timedelta(seconds=balance * 3600 // t.price)


def min_hourly_balance(t: Tariff) -> int:
    """Минимум на балансе для старта почасового тарифа (хватает на MIN_HOURLY_MINUTES минут)."""
    return math.ceil(t.price * config.MIN_HOURLY_MINUTES / 60)


def base_end_time(t: Tariff, start: datetime, utc_offset: int) -> datetime | None:
    """Конец оплаченного тарифа для пакета и ночи; у почасового его нет (он ограничен только балансом)."""
    if t.kind == "package":
        return start + timedelta(minutes=t.package_minutes)
    if t.kind == "night":
        return night_end_time(t, start, utc_offset)
    return None


def overtime_limit_end(base_end: datetime, balance: int, rate: int) -> datetime:
    """Когда кончатся деньги у сессии, которая после конца тарифа идёт с почасовой оплатой: на balance рублей при ставке
    rate ₽/час хватает balance*3600/rate секунд после base_end. Округляем вниз: стоимость никогда не превысит баланс."""
    return base_end + timedelta(seconds=max(balance, 0) * 3600 // rate)


def compute_planned_end(t: Tariff, start: datetime, utc_offset: int, balance: int | None = None) -> datetime | None:
    if t.kind == "package":
        return start + timedelta(minutes=t.package_minutes)
    if t.kind == "night":
        return night_end_time(t, start, utc_offset)
    if balance is not None:  # hourly: ограничен балансом гостя
        return hourly_limit_end(t, start, balance)
    return None  # hourly без баланса — без ограничения


def overtime_cost(s: Session, now: datetime) -> int:
    """Сколько набежало за время ПОСЛЕ конца пакета или ночи (по ставке, зафиксированной при старте). Целые числа, округление вверх."""
    if not s.base_end or not s.overtime_price:
        return 0
    end = min(s.ended_at or now, s.planned_end) if s.planned_end else (s.ended_at or now)
    micros = (end - s.base_end) // timedelta(microseconds=1)
    return -(-micros * s.overtime_price // 3_600_000_000) if micros > 0 else 0


def in_overtime(s: Session, now: datetime) -> bool:
    return bool(s.base_end and s.overtime_price and s.ended_at is None and now >= s.base_end)


def compute_cost(s: Session, now: datetime) -> int:
    t = s.tariff
    if t.kind in ("package", "night"):
        return t.price + (s.extra_paid or 0) + overtime_cost(s, now)
    end = s.ended_at or now
    if s.planned_end:
        end = min(end, s.planned_end)
    # Считаем целыми числами (микросекунды), а не float: иначе на «круглых» границах выходит 31.000000000000004 → 32 ₽
    # при балансе 31 ₽. Время идёт непрерывно, стоимость пропорциональна времени, округление вверх до рубля.
    micros = max((end - s.started_at) // timedelta(microseconds=1), 0)
    return -(-micros * t.price // 3_600_000_000)


def is_expired(s: Session, now: datetime) -> bool:
    return s.ended_at is None and s.planned_end is not None and now >= s.planned_end
