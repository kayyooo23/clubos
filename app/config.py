"""Все настройки из переменных окружения. Читаются один раз при импорте."""
import os

ENV = os.getenv("CLUB_ENV", "dev")  # dev | prod
DB_URL = os.getenv("CLUB_DB_URL", "sqlite:///./club.db")

_DEV_PLATFORM_KEY = "platform-dev-key"
PLATFORM_KEY = os.getenv("CLUB_PLATFORM_KEY", _DEV_PLATFORM_KEY)

STAFF_TOKEN_HOURS = int(os.getenv("CLUB_STAFF_TOKEN_HOURS", "12"))
PC_ONLINE_SECONDS = 30       # ПК считается «на связи», если heartbeat был не раньше
MAX_FAILED_LOGINS = 5        # неудачных входов подряд до блокировки
LOGIN_LOCK_MINUTES = 15
STAFF_PASSWORD_MIN = 10
GUEST_PASSWORD_MIN = 8
GUEST_TOKEN_HOURS = 12       # жёсткий срок входа гостя; кроме того выход по бездействию (clubs.idle_logout_minutes)

# SMS-подтверждение. Лимиты считаем по таблице phone_verifications (Redis не нужен).
SMS_PROVIDER = os.getenv("CLUB_SMS_PROVIDER", "console")  # console = код печатается в консоль сервера (только dev)
VERIFY_CODE_TTL_MINUTES = 5
VERIFY_MAX_ATTEMPTS = 5
SMS_RESEND_SECONDS = 60
SMS_PER_PHONE_PER_HOUR = 5
SMS_PER_PC_PER_HOUR = 20     # лимит по ПК, а не по IP: за NAT клуба у всех ПК один внешний адрес
SMS_PER_CLUB_PER_DAY = 300

STEAM_AUTOCOVERS = os.getenv("CLUB_STEAM_AUTOCOVERS", "1") != "0"   # сервер сам подтягивает постеры Steam играм без обложки (0: только кнопкой в админке)
STEAM_AUTOCOVERS_EVERY_SECONDS = 600                                 # не чаще раза в столько секунд на клуб, чтобы не нагружать Steam

BOOKING_LEAD_MINUTES = 30   # за сколько до начала брони ПК уже держится за гостем (чужая сессия не начнётся)
BOOKING_GRACE_MINUTES = 15   # сколько бронь «горит» после начала: не пришёл, ПК освобождается
MIN_HOURLY_MINUTES = 10      # для старта почасового тарифа на балансе должно хватать минимум на столько минут
MAX_TOPUP = 100_000          # потолок одной операции пополнения/корректировки (защита от опечаток)

if ENV == "prod" and PLATFORM_KEY == _DEV_PLATFORM_KEY:
    raise RuntimeError("В prod нужно задать CLUB_PLATFORM_KEY (не оставляй ключ по умолчанию)")
if ENV == "prod" and SMS_PROVIDER == "console":
    raise RuntimeError("В prod нужен реальный SMS-провайдер (CLUB_SMS_PROVIDER), консольная заглушка запрещена")
