import phonenumbers


def normalize_phone(raw: str) -> str:
    """'8 (912) 345-67-89' -> '+79123456789'. Без кода страны считаем номер российским."""
    try:
        number = phonenumbers.parse(raw, "RU")
    except phonenumbers.NumberParseException:
        raise ValueError("Некорректный номер телефона")
    if not phonenumbers.is_valid_number(number):
        raise ValueError("Некорректный номер телефона")
    return phonenumbers.format_number(number, phonenumbers.PhoneNumberFormat.E164)


def mask_phone(phone: str) -> str:
    """+79123456789 -> +7 *** ***-67-89 (для логов и экрана ПК)."""
    return f"{phone[:2]} *** ***-{phone[-4:-2]}-{phone[-2:]}" if len(phone) >= 8 else "***"
