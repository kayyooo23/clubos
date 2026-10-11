"""Проверка загружаемых картинок без сторонних библиотек: тип по первым байтам и размер из заголовка.
Принимаются только PNG и JPEG: их без лишних библиотек читает клиент на ПК, а подсунуть в них что-то исполняемое нельзя."""
import struct
from dataclasses import dataclass

from ..errors import ServiceError

MAX_BYTES = 2 * 1024 * 1024
MIN_SIDE, MAX_SIDE = 100, 4096

_PNG = b"\x89PNG\r\n\x1a\n"
# маркеры JPEG, после которых идут размеры кадра (SOF), кроме DHT, JPG и DAC
_SOF = {0xC0, 0xC1, 0xC2, 0xC3, 0xC5, 0xC6, 0xC7, 0xC9, 0xCA, 0xCB, 0xCD, 0xCE, 0xCF}


@dataclass(frozen=True)
class ImageInfo:
    content_type: str
    width: int
    height: int


def _bad(detail: str) -> ServiceError:
    return ServiceError(422, detail)


def _png_size(data: bytes) -> tuple[int, int]:
    if len(data) < 24 or data[12:16] != b"IHDR":
        raise _bad("Файл повреждён: не найден заголовок PNG")
    return struct.unpack(">II", data[16:24])


def _jpeg_size(data: bytes) -> tuple[int, int]:
    i, n = 2, len(data)
    while i + 4 <= n:
        if data[i] != 0xFF:
            i += 1
            continue
        while i < n and data[i] == 0xFF:      # допускаются повторяющиеся 0xFF перед маркером
            i += 1
        if i >= n:
            break
        marker = data[i]
        i += 1
        if marker in (0xD8, 0x01) or 0xD0 <= marker <= 0xD7:   # маркеры без длины
            continue
        if marker == 0xD9:
            break
        if i + 2 > n:
            break
        length = struct.unpack(">H", data[i:i + 2])[0]
        if marker in _SOF:
            if i + 7 > n or length < 8:
                break
            height, width = struct.unpack(">HH", data[i + 3:i + 7])
            return width, height
        i += length
    raise _bad("Файл повреждён: не найден размер JPEG")


def inspect_image(data: bytes, *, min_side: int = MIN_SIDE) -> ImageInfo:
    """Тип и размеры картинки или понятная ошибка (422). Лишнее в файле (GIF, WebP, SVG) отклоняется."""
    if not data:
        raise _bad("Файл пустой")
    if len(data) > MAX_BYTES:
        raise ServiceError(413, f"Файл больше {MAX_BYTES // (1024 * 1024)} МБ: сожмите картинку")
    if data.startswith(_PNG):
        content_type, (width, height) = "image/png", _png_size(data)
    elif data.startswith(b"\xff\xd8\xff"):
        content_type, (width, height) = "image/jpeg", _jpeg_size(data)
    else:
        raise ServiceError(415, "Нужна картинка PNG или JPEG (GIF, WebP и SVG не подходят)")
    if not (min_side <= width <= MAX_SIDE and min_side <= height <= MAX_SIDE):
        raise _bad(f"Размер {width} x {height} не подходит: каждая сторона от {min_side} до {MAX_SIDE} пикселей")
    return ImageInfo(content_type, width, height)
