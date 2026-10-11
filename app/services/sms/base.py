from typing import Protocol


class SmsError(Exception):
    """Провайдер не смог отправить SMS."""


class SmsProvider(Protocol):
    def send(self, phone: str, text: str) -> None:
        """Отправляет SMS на номер в формате E.164. При неудаче бросает SmsError."""
