import logging

from ...phone import mask_phone

log = logging.getLogger("uvicorn.error")  # попадает в то же окно, где запущен сервер


class ConsoleSms:
    """Заглушка для разработки: «SMS» печатается в консоль сервера, никуда не уходит."""

    def send(self, phone: str, text: str) -> None:
        log.warning("[SMS-заглушка → %s] %s", mask_phone(phone), text)
