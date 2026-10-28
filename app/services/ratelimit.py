"""Ограничитель неудачных попыток (подбор кодов). Хранится в памяти процесса: при нескольких процессах сервера у каждого свой счётчик,
поэтому для боевой работы за балансировщиком нужен общий счётчик (Redis или таблица в БД). Для одного процесса этого достаточно."""
import threading
import time
from collections import defaultdict, deque

from ..errors import ServiceError


class FailureLimiter:
    def __init__(self, limit: int, window_seconds: int, message: str):
        self._limit, self._window, self._message = limit, window_seconds, message
        self._hits: dict[str, deque[float]] = defaultdict(deque)
        self._lock = threading.Lock()

    def _trim(self, key: str, now: float) -> deque[float]:
        hits = self._hits[key]
        while hits and now - hits[0] > self._window:
            hits.popleft()
        return hits

    def check(self, key: str) -> None:
        """Отказывает (429), если с этого адреса уже слишком много неудач подряд."""
        with self._lock:
            if len(self._trim(key, time.monotonic())) >= self._limit:
                raise ServiceError(429, self._message)

    def fail(self, key: str) -> None:
        with self._lock:
            now = time.monotonic()
            self._trim(key, now).append(now)

    def reset(self) -> None:
        with self._lock:
            self._hits.clear()


enroll_failures = FailureLimiter(15, 600, "Слишком много неверных кодов подключения. Подождите несколько минут")
