"""SMS-провайдер выбирается настройкой CLUB_SMS_PROVIDER. Чтобы подключить реального, добавь файл с классом,
у которого есть send(phone, text), и ветку в _build(). Остальной код про провайдера ничего не знает."""
from functools import lru_cache

from ... import config
from .base import SmsError, SmsProvider  # noqa: F401
from .console import ConsoleSms


@lru_cache
def _build() -> SmsProvider:
    if config.SMS_PROVIDER == "console":
        return ConsoleSms()
    raise RuntimeError(f"Неизвестный SMS-провайдер: {config.SMS_PROVIDER!r}")


def get_sms() -> SmsProvider:
    """Зависимость FastAPI (в тестах подменяется на запоминающую заглушку)."""
    return _build()
