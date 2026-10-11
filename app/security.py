"""Пароли и секреты. Пароли — argon2id. Токены входа и ключи ПК — случайные строки,
в БД хранится только их sha256 (утечка БД не даёт ходить по чужим токенам)."""
import hashlib
import secrets

from argon2 import PasswordHasher
from argon2.exceptions import InvalidHashError, VerificationError

_ph = PasswordHasher()
_DUMMY_HASH = _ph.hash("dummy-password-for-timing")


def hash_password(password: str) -> str:
    return _ph.hash(password)


def verify_password(password: str, password_hash: str) -> bool:
    try:
        return _ph.verify(password_hash, password)
    except (VerificationError, InvalidHashError):  # VerifyMismatchError — подкласс VerificationError
        return False


def burn_password_check(password: str) -> None:
    """Для несуществующего аккаунта тратим столько же времени, сколько на настоящую проверку,
    чтобы по скорости ответа нельзя было понять, есть ли такой номер."""
    verify_password(password, _DUMMY_HASH)


def new_secret() -> str:
    return secrets.token_urlsafe(32)


def hash_secret(secret: str) -> str:
    return hashlib.sha256(secret.encode()).hexdigest()
