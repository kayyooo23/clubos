"""Все таблицы. Правило мультиклубности: в каждой таблице с данными клуба есть club_id,
а запросы всегда фильтруются по club_id, который сервер берёт из ключа ПК или токена (никогда из запроса)."""
from datetime import date, datetime

from sqlalchemy import (BigInteger, Boolean, CheckConstraint, Date, DateTime, ForeignKey, Index, Integer, LargeBinary, String, Text,
                        UniqueConstraint, text)
from sqlalchemy.orm import Mapped, mapped_column, relationship

from .db import Base
from .timeutil import utcnow


class Club(Base):
    """Клиент платформы — один клуб. slug — публичный код клуба для входа в админку."""
    __tablename__ = "clubs"
    __table_args__ = (CheckConstraint("status in ('active', 'suspended')", name="ck_clubs_status"),)
    id: Mapped[int] = mapped_column(primary_key=True)
    slug: Mapped[str] = mapped_column(String(40), unique=True)
    name: Mapped[str] = mapped_column(String(100))
    utc_offset: Mapped[int] = mapped_column(Integer, default=3)  # часовой пояс клуба, часы от UTC
    status: Mapped[str] = mapped_column(String(10), default="active")
    # реклама партнёров на экране блокировки: клуб включает сам, ads_club_share = доля клуба в доходе от рекламы, проценты
    ads_enabled: Mapped[bool] = mapped_column(Boolean, default=False, server_default="0")
    ads_club_share: Mapped[int] = mapped_column(Integer, default=30, server_default="30")
    idle_logout_minutes: Mapped[int] = mapped_column(Integer, default=5, server_default="5")  # выход гостя из кабинета без игры
    created_at: Mapped[datetime] = mapped_column(DateTime, default=utcnow)


class Zone(Base):
    """Зал клуба как шаблон для ПК: обычный зал, VIP, Premium, PS5, Duo. У зала свои характеристики (что за ПК и периферия) и свои тарифы,
    поэтому при добавлении ПК достаточно выбрать зал: цены и характеристики вводить заново не нужно. Название зала же служит залом на карте клуба
    (pcs.zone). Тарифы зала: tariffs.zone_id (см. services/zones.py)."""
    __tablename__ = "zones"
    __table_args__ = (UniqueConstraint("club_id", "name", name="uq_zones_club_name"),)
    id: Mapped[int] = mapped_column(primary_key=True)
    club_id: Mapped[int] = mapped_column(ForeignKey("clubs.id"), index=True)
    name: Mapped[str] = mapped_column(String(40))
    cpu: Mapped[str | None] = mapped_column(String(100), nullable=True)
    gpu: Mapped[str | None] = mapped_column(String(100), nullable=True)
    ram: Mapped[str | None] = mapped_column(String(100), nullable=True)
    monitor: Mapped[str | None] = mapped_column(String(100), nullable=True)
    peripherals: Mapped[str | None] = mapped_column(String(200), nullable=True)   # мышь, клавиатура, наушники, кресло
    note: Mapped[str | None] = mapped_column(String(200), nullable=True)
    sort_order: Mapped[int] = mapped_column(Integer, default=100, server_default="100")
    created_at: Mapped[datetime] = mapped_column(DateTime, default=utcnow)


class EnrollCode(Base):
    """Код подключения ПК: администратор создаёт его, установщик ставит клиент с этим кодом, и ПК при первом запуске сам регистрируется
    в клубе и получает свой ключ (см. services/enroll.py). В базе только хеш кода; у кода срок, число ПК и (необязательно) зал для новых ПК."""
    __tablename__ = "enroll_codes"
    id: Mapped[int] = mapped_column(primary_key=True)
    club_id: Mapped[int] = mapped_column(ForeignKey("clubs.id"), index=True)
    code_hash: Mapped[str] = mapped_column(String(64), unique=True)
    code_hint: Mapped[str] = mapped_column(String(8))          # последние знаки кода: чтобы отличать коды в списке
    label: Mapped[str | None] = mapped_column(String(80), nullable=True)
    zone_id: Mapped[int | None] = mapped_column(ForeignKey("zones.id"), nullable=True)
    created_by: Mapped[int | None] = mapped_column(ForeignKey("accounts.id"), nullable=True)
    created_at: Mapped[datetime] = mapped_column(DateTime, default=utcnow)
    expires_at: Mapped[datetime] = mapped_column(DateTime)
    max_uses: Mapped[int | None] = mapped_column(Integer, nullable=True)   # сколько ПК можно подключить; пусто: сколько угодно до конца срока
    used_count: Mapped[int] = mapped_column(Integer, default=0, server_default="0")
    revoked_at: Mapped[datetime | None] = mapped_column(DateTime, nullable=True)


class PC(Base):
    __tablename__ = "pcs"
    __table_args__ = (UniqueConstraint("club_id", "name", name="uq_pcs_club_name"),)
    id: Mapped[int] = mapped_column(primary_key=True)
    club_id: Mapped[int] = mapped_column(ForeignKey("clubs.id"), index=True)
    name: Mapped[str] = mapped_column(String(50))
    key_hash: Mapped[str] = mapped_column(String(64), unique=True)  # sha256 ключа ПК; сам ключ не хранится
    last_seen: Mapped[datetime | None] = mapped_column(DateTime, nullable=True)
    # карта клуба: зал и клетка (колонка, ряд) на сетке; без клетки ПК лежит «в стопке» под картой
    zone: Mapped[str] = mapped_column(String(40), default="Общий зал", server_default="Общий зал")
    # шаблон зала (характеристики и тарифы); pcs.zone всегда равно его названию. Пусто: ПК без шаблона, ему подходят общие тарифы клуба
    zone_id: Mapped[int | None] = mapped_column(ForeignKey("zones.id"), nullable=True, index=True)
    pos_x: Mapped[int | None] = mapped_column(Integer, nullable=True)
    pos_y: Mapped[int | None] = mapped_column(Integer, nullable=True)
    note: Mapped[str | None] = mapped_column(String(200), nullable=True)  # заметка сотрудника («мышка залита»)
    maintenance: Mapped[bool] = mapped_column(Boolean, default=False, server_default="0")  # на обслуживании: гостей не пускаем
    # железо присылает служба на ПК (раз в несколько часов): JSON с процессором, памятью, видеокартой, дисками, сетью
    hw_json: Mapped[str | None] = mapped_column(Text, nullable=True)
    hw_updated_at: Mapped[datetime | None] = mapped_column(DateTime, nullable=True)
    hw_changed_at: Mapped[datetime | None] = mapped_column(DateTime, nullable=True)  # конфигурация изменилась; сбрасывает сотрудник
    # программы, найденные на ПК (JSON: имя и полный путь к exe): из этого списка администратор выбирает программу для меню гостя
    programs_json: Mapped[str | None] = mapped_column(Text, nullable=True)
    programs_at: Mapped[datetime | None] = mapped_column(DateTime, nullable=True)
    programs_wanted_at: Mapped[datetime | None] = mapped_column(DateTime, nullable=True)  # администратор попросил обновить список
    ads_seen_at: Mapped[datetime | None] = mapped_column(DateTime, nullable=True)  # с какого момента считаем время показа рекламы на этом ПК
    # отпечаток железа (хеш UUID системы и серийного номера платы), которым клиент подписывает запросы: ключ ПК привязан к своему компьютеру, у скопированного образа он другой
    machine_id: Mapped[str | None] = mapped_column(String(64), nullable=True)
    enrolled_at: Mapped[datetime | None] = mapped_column(DateTime, nullable=True)      # ПК подключён по коду, а не создан вручную
    enroll_code_id: Mapped[int | None] = mapped_column(ForeignKey("enroll_codes.id"), nullable=True)
    clone_alert_at: Mapped[datetime | None] = mapped_column(DateTime, nullable=True)   # с этим ключом пробовал работать другой компьютер (скопированный образ)
    # версия клиента и итог последнего автообновления (присылает служба): по ним администратор и владелец платформы видят, кто на какой версии
    client_version: Mapped[str | None] = mapped_column(String(20), nullable=True)
    client_version_at: Mapped[datetime | None] = mapped_column(DateTime, nullable=True)
    update_status: Mapped[str | None] = mapped_column(String(12), nullable=True)       # ok | failed | rolled_back
    update_message: Mapped[str | None] = mapped_column(String(200), nullable=True)
    update_at: Mapped[datetime | None] = mapped_column(DateTime, nullable=True)
    # игры в игровой папке этого ПК (JSON: путь, название, размер): из них администратор выбирает, что синхронизировать на другие ПК
    games_json: Mapped[str | None] = mapped_column(Text, nullable=True)
    games_at: Mapped[datetime | None] = mapped_column(DateTime, nullable=True)
    games_wanted_at: Mapped[datetime | None] = mapped_column(DateTime, nullable=True)   # администратор попросил обновить список


class Tariff(Base):
    """kind: hourly  - почасовой, платим за фактическое время (поминутно)
       package - пакет: фиксированная цена за package_minutes минут
       night   - ночной: фиксированная цена, действует с night_start до night_end (местное время клуба)"""
    __tablename__ = "tariffs"
    id: Mapped[int] = mapped_column(primary_key=True)
    club_id: Mapped[int] = mapped_column(ForeignKey("clubs.id"), index=True)
    name: Mapped[str] = mapped_column(String(80))
    kind: Mapped[str] = mapped_column(String(10))
    price: Mapped[int] = mapped_column(Integer)  # руб: за час (hourly) или за весь пакет/ночь
    package_minutes: Mapped[int | None] = mapped_column(Integer, nullable=True)
    night_start: Mapped[int | None] = mapped_column(Integer, nullable=True)  # час 0-23
    night_end: Mapped[int | None] = mapped_column(Integer, nullable=True)
    active: Mapped[bool] = mapped_column(Boolean, default=True)
    # зал, которому принадлежит тариф. Пусто: общий тариф клуба. Если у зала есть свои действующие тарифы, ПК зала видят только их, иначе общие
    zone_id: Mapped[int | None] = mapped_column(ForeignKey("zones.id"), nullable=True, index=True)
    # цены по дням недели (JSON {"4": 200, "5": 250}: 0 = понедельник ... 6 = воскресенье); дней, которых нет, действует price
    day_prices: Mapped[str | None] = mapped_column(Text, nullable=True)


class CatalogApp(Base):
    """Игра или программа в меню гостя. Каталог хранит сервер: у всех ПК клуба один и тот же список.
    launch_kind: steam - запуск через Steam по номеру игры (launch_target = app id, обновлением занимается Steam)
                 exe   - запуск файла по полному пути на ПК (launch_target = C:\\...\\game.exe)
    Гость путь не вводит и аргументов не передаёт: клиент запускает только записи отсюда."""
    __tablename__ = "catalog_apps"
    __table_args__ = (
        CheckConstraint("tab in ('games', 'apps')", name="ck_catalog_apps_tab"),
        CheckConstraint("launch_kind in ('steam', 'exe')", name="ck_catalog_apps_launch_kind"),
    )
    id: Mapped[int] = mapped_column(primary_key=True)
    club_id: Mapped[int] = mapped_column(ForeignKey("clubs.id"), index=True)
    title: Mapped[str] = mapped_column(String(80))
    tab: Mapped[str] = mapped_column(String(10), default="games")
    category: Mapped[str] = mapped_column(String(40), default="Другое")
    age_rating: Mapped[int | None] = mapped_column(Integer, nullable=True)  # 0, 6, 12, 16, 18
    launch_kind: Mapped[str] = mapped_column(String(10))
    launch_target: Mapped[str] = mapped_column(String(260))
    sort_order: Mapped[int] = mapped_column(Integer, default=100, server_default="100")
    active: Mapped[bool] = mapped_column(Boolean, default=True, server_default="1")
    created_at: Mapped[datetime] = mapped_column(DateTime, default=utcnow)


class Account(Base):
    """Аккаунт внутри клуба: гость или сотрудник. Один человек в двух клубах = два аккаунта."""
    __tablename__ = "accounts"
    __table_args__ = (
        UniqueConstraint("club_id", "phone", name="uq_accounts_club_phone"),
        CheckConstraint("role in ('guest', 'staff')", name="ck_accounts_role"),
        CheckConstraint("status in ('active', 'blocked')", name="ck_accounts_status"),
        CheckConstraint("balance >= 0", name="ck_accounts_balance"),
    )
    id: Mapped[int] = mapped_column(primary_key=True)
    club_id: Mapped[int] = mapped_column(ForeignKey("clubs.id"), index=True)
    phone: Mapped[str] = mapped_column(String(20))  # E.164: +79123456789
    password_hash: Mapped[str] = mapped_column(String(255))
    role: Mapped[str] = mapped_column(String(10))
    balance: Mapped[int] = mapped_column(Integer, default=0)  # целые рубли
    status: Mapped[str] = mapped_column(String(10), default="active")
    phone_verified_at: Mapped[datetime | None] = mapped_column(DateTime, nullable=True)
    consent_at: Mapped[datetime | None] = mapped_column(DateTime, nullable=True)  # согласие на обработку персональных данных
    failed_logins: Mapped[int] = mapped_column(Integer, default=0)
    locked_until: Mapped[datetime | None] = mapped_column(DateTime, nullable=True)
    created_at: Mapped[datetime] = mapped_column(DateTime, default=utcnow)


class Login(Base):
    """Кто сейчас вошёл. Токен непрозрачный (случайная строка), в БД только хеш: выход обнуляет его мгновенно.
    pc_id заполнен для входа на ПК (гость → кабинет, сотрудник → рабочий стол), пуст для входа в админку."""
    __tablename__ = "logins"
    __table_args__ = (
        # на одном ПК не больше одного активного входа, даже если два запроса пришли одновременно
        Index("uq_logins_one_active_per_pc", "pc_id", unique=True,
              sqlite_where=text("ended_at IS NULL"), postgresql_where=text("ended_at IS NULL")),
    )
    id: Mapped[int] = mapped_column(primary_key=True)
    club_id: Mapped[int] = mapped_column(ForeignKey("clubs.id"), index=True)
    account_id: Mapped[int] = mapped_column(ForeignKey("accounts.id"), index=True)
    pc_id: Mapped[int | None] = mapped_column(ForeignKey("pcs.id"), nullable=True)
    token_hash: Mapped[str] = mapped_column(String(64), unique=True)
    created_at: Mapped[datetime] = mapped_column(DateTime, default=utcnow)
    last_seen_at: Mapped[datetime] = mapped_column(DateTime, default=utcnow)
    expires_at: Mapped[datetime] = mapped_column(DateTime)
    ended_at: Mapped[datetime | None] = mapped_column(DateTime, nullable=True)


class Session(Base):
    """Игровая сессия гостя на ПК. Сессии без аккаунта не бывает: играют только зарегистрированные гости."""
    __tablename__ = "sessions"
    __table_args__ = (
        # на одном ПК не может быть двух незавершённых сессий, даже если два запроса пришли одновременно
        Index("uq_sessions_one_active_per_pc", "pc_id", unique=True,
              sqlite_where=text("ended_at IS NULL"), postgresql_where=text("ended_at IS NULL")),
        # и у одного гостя — не больше одной незавершённой сессии (на балансе нельзя играть на двух ПК)
        Index("uq_sessions_one_active_per_account", "account_id", unique=True,
              sqlite_where=text("ended_at IS NULL"), postgresql_where=text("ended_at IS NULL")),
    )
    id: Mapped[int] = mapped_column(primary_key=True)
    club_id: Mapped[int] = mapped_column(ForeignKey("clubs.id"), index=True)
    pc_id: Mapped[int] = mapped_column(ForeignKey("pcs.id"))
    tariff_id: Mapped[int] = mapped_column(ForeignKey("tariffs.id"))
    account_id: Mapped[int] = mapped_column(ForeignKey("accounts.id"))
    started_at: Mapped[datetime] = mapped_column(DateTime)
    planned_end: Mapped[datetime | None] = mapped_column(DateTime, nullable=True)  # самый поздний конец: когда кончится оплаченное время или деньги
    # Пакет и ночь: base_end = конец оплаченного тарифа. После него сессия идёт дальше с почасовой оплатой по ставке overtime_price
    # (₽/час, берётся при старте), пока хватает баланса; planned_end тогда = base_end + время, на которое хватает баланса.
    base_end: Mapped[datetime | None] = mapped_column(DateTime, nullable=True)
    overtime_price: Mapped[int | None] = mapped_column(Integer, nullable=True)
    # Цена тарифа, зафиксированная при старте по дню недели (₽/час или за пакет/ночь). Пусто у старых сессий: тогда цена тарифа.
    price: Mapped[int | None] = mapped_column(Integer, nullable=True)
    # Сколько уже списано сверх цены стартового тарифа: продления (пакеты, купленные посреди игры) и доплата по часам, которую пришлось
    # закрыть перед продлением. Итоговая стоимость сессии = цена стартового тарифа + extra_paid + доплата после последнего base_end.
    extra_paid: Mapped[int] = mapped_column(Integer, default=0, server_default="0")
    ended_at: Mapped[datetime | None] = mapped_column(DateTime, nullable=True)
    cost: Mapped[int | None] = mapped_column(Integer, nullable=True)  # фиксируется при закрытии

    pc: Mapped[PC] = relationship()
    tariff: Mapped[Tariff] = relationship()


class PhoneVerification(Base):
    """Код из SMS (регистрация или сброс пароля). Код хранится только хешем. Строки создаются и для номеров,
    которым SMS не отправляется (уже зарегистрирован / нет аккаунта) — чтобы лимиты и ответы не выдавали, кто в базе."""
    __tablename__ = "phone_verifications"
    __table_args__ = (
        Index("ix_phone_verifications_lookup", "club_id", "phone", "purpose"),
        CheckConstraint("purpose in ('register', 'reset')", name="ck_phone_verifications_purpose"),
    )
    id: Mapped[int] = mapped_column(primary_key=True)
    club_id: Mapped[int] = mapped_column(ForeignKey("clubs.id"), index=True)
    phone: Mapped[str] = mapped_column(String(20))
    purpose: Mapped[str] = mapped_column(String(10))
    code_hash: Mapped[str] = mapped_column(String(64))
    expires_at: Mapped[datetime] = mapped_column(DateTime)
    attempts: Mapped[int] = mapped_column(Integer, default=0)
    consumed_at: Mapped[datetime | None] = mapped_column(DateTime, nullable=True)
    created_at: Mapped[datetime] = mapped_column(DateTime, default=utcnow)
    ip: Mapped[str | None] = mapped_column(String(45), nullable=True)
    pc_id: Mapped[int | None] = mapped_column(ForeignKey("pcs.id"), nullable=True)


class WalletTransaction(Base):
    """Журнал денег: только добавление, не правится и не удаляется. accounts.balance — кэш суммы этого журнала."""
    __tablename__ = "wallet_transactions"
    __table_args__ = (
        CheckConstraint("kind in ('topup_cash', 'topup_online', 'session_charge', 'adjustment', 'shop_purchase', 'shop_refund')",
                        name="ck_wallet_transactions_kind"),
        CheckConstraint("amount != 0", name="ck_wallet_transactions_amount"),
    )
    id: Mapped[int] = mapped_column(primary_key=True)
    club_id: Mapped[int] = mapped_column(ForeignKey("clubs.id"), index=True)
    account_id: Mapped[int] = mapped_column(ForeignKey("accounts.id"), index=True)
    amount: Mapped[int] = mapped_column(Integer)  # + пополнение / − списание, целые рубли
    balance_after: Mapped[int] = mapped_column(Integer)
    kind: Mapped[str] = mapped_column(String(20))
    session_id: Mapped[int | None] = mapped_column(ForeignKey("sessions.id"), nullable=True)
    staff_id: Mapped[int | None] = mapped_column(ForeignKey("accounts.id"), nullable=True)
    comment: Mapped[str | None] = mapped_column(String(200), nullable=True)
    created_at: Mapped[datetime] = mapped_column(DateTime, default=utcnow)


class AuditLog(Base):
    """Кто что сделал: вход сотрудника на рабочий стол, пополнения, блокировки. Без личных данных в details."""
    __tablename__ = "audit_log"
    id: Mapped[int] = mapped_column(primary_key=True)
    club_id: Mapped[int] = mapped_column(ForeignKey("clubs.id"), index=True)
    actor_account_id: Mapped[int | None] = mapped_column(ForeignKey("accounts.id"), nullable=True)
    pc_id: Mapped[int | None] = mapped_column(ForeignKey("pcs.id"), nullable=True)
    action: Mapped[str] = mapped_column(String(40))
    details: Mapped[str | None] = mapped_column(String(500), nullable=True)
    ip: Mapped[str | None] = mapped_column(String(45), nullable=True)
    created_at: Mapped[datetime] = mapped_column(DateTime, default=utcnow)


class Product(Base):
    """Товар в баре клуба (еда, напитки). Гость заказывает с ПК, платит с баланса. Скрытый товар (active = false) гостю не виден."""
    __tablename__ = "products"
    __table_args__ = (CheckConstraint("price > 0", name="ck_products_price"),)
    id: Mapped[int] = mapped_column(primary_key=True)
    club_id: Mapped[int] = mapped_column(ForeignKey("clubs.id"), index=True)
    name: Mapped[str] = mapped_column(String(80))
    category: Mapped[str] = mapped_column(String(40), default="Напитки")
    price: Mapped[int] = mapped_column(Integer)  # целые рубли за штуку
    sort_order: Mapped[int] = mapped_column(Integer, default=100, server_default="100")
    active: Mapped[bool] = mapped_column(Boolean, default=True, server_default="1")
    created_at: Mapped[datetime] = mapped_column(DateTime, default=utcnow)


class Order(Base):
    """Заказ гостя из бара. Деньги списываются при создании (wallet kind shop_purchase), при отмене возвращаются (shop_refund).
    status: new - ждёт сотрудника, accepted - готовят, done - выдан, cancelled - отменён (деньги возвращены)."""
    __tablename__ = "orders"
    __table_args__ = (
        CheckConstraint("status in ('new', 'accepted', 'done', 'cancelled')", name="ck_orders_status"),
        CheckConstraint("total > 0", name="ck_orders_total"),
    )
    id: Mapped[int] = mapped_column(primary_key=True)
    club_id: Mapped[int] = mapped_column(ForeignKey("clubs.id"), index=True)
    pc_id: Mapped[int] = mapped_column(ForeignKey("pcs.id"))  # за какой стол/ПК нести заказ
    account_id: Mapped[int] = mapped_column(ForeignKey("accounts.id"), index=True)
    total: Mapped[int] = mapped_column(Integer)
    status: Mapped[str] = mapped_column(String(10), default="new")
    comment: Mapped[str | None] = mapped_column(String(200), nullable=True)
    created_at: Mapped[datetime] = mapped_column(DateTime, default=utcnow)
    updated_at: Mapped[datetime] = mapped_column(DateTime, default=utcnow)
    handled_by: Mapped[int | None] = mapped_column(ForeignKey("accounts.id"), nullable=True)  # кто принял, выдал или отменил

    items: Mapped[list["OrderItem"]] = relationship(cascade="all, delete-orphan", order_by="OrderItem.id")


class OrderItem(Base):
    """Строка заказа. Название и цена копируются в момент заказа: правка меню потом не меняет старые заказы."""
    __tablename__ = "order_items"
    __table_args__ = (CheckConstraint("qty > 0", name="ck_order_items_qty"),)
    id: Mapped[int] = mapped_column(primary_key=True)
    order_id: Mapped[int] = mapped_column(ForeignKey("orders.id"), index=True)
    product_id: Mapped[int | None] = mapped_column(ForeignKey("products.id"), nullable=True)
    name: Mapped[str] = mapped_column(String(80))
    price: Mapped[int] = mapped_column(Integer)
    qty: Mapped[int] = mapped_column(Integer)


class Call(Base):
    """Вызов администратора с ПК. На одном ПК не больше одного открытого вызова: повторное нажатие ничего не плодит."""
    __tablename__ = "calls"
    __table_args__ = (
        CheckConstraint("status in ('open', 'done')", name="ck_calls_status"),
        Index("uq_calls_one_open_per_pc", "pc_id", unique=True,
              sqlite_where=text("status = 'open'"), postgresql_where=text("status = 'open'")),
    )
    id: Mapped[int] = mapped_column(primary_key=True)
    club_id: Mapped[int] = mapped_column(ForeignKey("clubs.id"), index=True)
    pc_id: Mapped[int] = mapped_column(ForeignKey("pcs.id"))
    account_id: Mapped[int | None] = mapped_column(ForeignKey("accounts.id"), nullable=True)
    text: Mapped[str | None] = mapped_column(String(100), nullable=True)
    status: Mapped[str] = mapped_column(String(6), default="open")
    created_at: Mapped[datetime] = mapped_column(DateTime, default=utcnow)
    handled_at: Mapped[datetime | None] = mapped_column(DateTime, nullable=True)
    handled_by: Mapped[int | None] = mapped_column(ForeignKey("accounts.id"), nullable=True)


class PcCommand(Base):
    """Команда ПК от администратора (перезагрузить, выключить). Служба на ПК забирает её с очередным опросом и подтверждает результат.
    status: pending - ждёт ПК, sent - отдана ПК, done - выполнена, failed - не вышло, cancelled - отменена или истёк срок.
    Команда живёт недолго (expires_at): перезагрузка, поставленная вчера, сегодня уже никому не нужна."""
    __tablename__ = "pc_commands"
    __table_args__ = (CheckConstraint("status in ('pending', 'sent', 'done', 'failed', 'cancelled')", name="ck_pc_commands_status"),)
    id: Mapped[int] = mapped_column(primary_key=True)
    club_id: Mapped[int] = mapped_column(ForeignKey("clubs.id"), index=True)
    pc_id: Mapped[int] = mapped_column(ForeignKey("pcs.id"), index=True)
    kind: Mapped[str] = mapped_column(String(30))
    payload: Mapped[str | None] = mapped_column(String(500), nullable=True)   # JSON с параметрами (для перезагрузки и выключения пуст)
    status: Mapped[str] = mapped_column(String(10), default="pending")
    attempts: Mapped[int] = mapped_column(Integer, default=0, server_default="0")   # сколько раз отдавалась ПК
    created_by: Mapped[int | None] = mapped_column(ForeignKey("accounts.id"), nullable=True)
    created_at: Mapped[datetime] = mapped_column(DateTime, default=utcnow)
    sent_at: Mapped[datetime | None] = mapped_column(DateTime, nullable=True)
    finished_at: Mapped[datetime | None] = mapped_column(DateTime, nullable=True)
    result: Mapped[str | None] = mapped_column(String(300), nullable=True)
    expires_at: Mapped[datetime] = mapped_column(DateTime)


class PcNotice(Base):
    """Сообщение администратора гостю на экран ПК («Пожалуйста, постарайтесь вести себя потише»). Любой текст до 300 знаков.
    Клиент забирает его с очередным опросом состояния ПК, пока оно свежее (см. services/notices.py); delivered_at - когда ПК получил его в первый раз."""
    __tablename__ = "pc_notices"
    id: Mapped[int] = mapped_column(primary_key=True)
    club_id: Mapped[int] = mapped_column(ForeignKey("clubs.id"), index=True)
    pc_id: Mapped[int] = mapped_column(ForeignKey("pcs.id"), index=True)
    text: Mapped[str] = mapped_column(String(300))
    created_by: Mapped[int | None] = mapped_column(ForeignKey("accounts.id"), nullable=True)
    created_at: Mapped[datetime] = mapped_column(DateTime, default=utcnow)
    delivered_at: Mapped[datetime | None] = mapped_column(DateTime, nullable=True)


class AppCover(Base):
    """Обложка игры в меню (постер PNG или JPEG, до 2 МБ). Лежит отдельно от записи каталога, чтобы списки не тянули байты.
    source: steam (подтянута из Steam), program (иконка программы, которую прислал ПК) или manual (загружена вручную, главнее автоматической);
    version = отпечаток для кэша клиента."""
    __tablename__ = "app_covers"
    __table_args__ = (CheckConstraint("source in ('steam', 'manual', 'program')", name="ck_app_covers_source"),)
    app_id: Mapped[int] = mapped_column(ForeignKey("catalog_apps.id"), primary_key=True)
    content_type: Mapped[str] = mapped_column(String(20))
    width: Mapped[int] = mapped_column(Integer)
    height: Mapped[int] = mapped_column(Integer)
    version: Mapped[str] = mapped_column(String(16))
    source: Mapped[str] = mapped_column(String(10))
    data: Mapped[bytes] = mapped_column(LargeBinary)
    created_at: Mapped[datetime] = mapped_column(DateTime, default=utcnow)


class Booking(Base):
    """Бронь ПК на время. Держит ПК за гостем (по номеру телефона): пока бронь «горит», сессию на этом ПК может начать только он.
    status: active - действует, used - гость пришёл и начал сессию, cancelled - отменена сотрудником.
    Брони, которые никто не занял и срок которых вышел, остаются active, но уже ничего не держат (см. services/bookings.py)."""
    __tablename__ = "bookings"
    __table_args__ = (
        CheckConstraint("status in ('active', 'cancelled', 'used')", name="ck_bookings_status"),
        CheckConstraint("ends_at > starts_at", name="ck_bookings_period"),
    )
    id: Mapped[int] = mapped_column(primary_key=True)
    club_id: Mapped[int] = mapped_column(ForeignKey("clubs.id"), index=True)
    pc_id: Mapped[int] = mapped_column(ForeignKey("pcs.id"), index=True)
    phone: Mapped[str] = mapped_column(String(20))  # E.164, как у гостей
    name: Mapped[str | None] = mapped_column(String(80), nullable=True)
    comment: Mapped[str | None] = mapped_column(String(200), nullable=True)
    starts_at: Mapped[datetime] = mapped_column(DateTime)
    ends_at: Mapped[datetime] = mapped_column(DateTime)
    status: Mapped[str] = mapped_column(String(10), default="active")
    created_by: Mapped[int | None] = mapped_column(ForeignKey("accounts.id"), nullable=True)
    created_at: Mapped[datetime] = mapped_column(DateTime, default=utcnow)


class Ad(Base):
    """Рекламный баннер партнёра. Общий для всех клубов платформы (club_id нет): его заводит владелец платформы и загружает картинку (AdImage).
    text это внутреннее название для владельца («Мама Рома, октябрь»), гостю оно не показывается. Баннеры крутятся по очереди, каждый
    duration_seconds секунд. advertiser и erid нужны для маркировки: под баннером гость видит «Реклама», рекламодателя и erid, если они заполнены."""
    __tablename__ = "ads"
    id: Mapped[int] = mapped_column(primary_key=True)
    text: Mapped[str] = mapped_column(String(200))
    advertiser: Mapped[str | None] = mapped_column(String(120), nullable=True)
    erid: Mapped[str | None] = mapped_column(String(80), nullable=True)
    active: Mapped[bool] = mapped_column(Boolean, default=True, server_default="1")
    starts_at: Mapped[datetime | None] = mapped_column(DateTime, nullable=True)   # UTC; пусто = с самого начала
    ends_at: Mapped[datetime | None] = mapped_column(DateTime, nullable=True)     # UTC; пусто = без конца
    sort_order: Mapped[int] = mapped_column(Integer, default=100, server_default="100")
    duration_seconds: Mapped[int] = mapped_column(Integer, default=10, server_default="10")
    created_at: Mapped[datetime] = mapped_column(DateTime, default=utcnow)


class AdImage(Base):
    """Картинка баннера (PNG или JPEG, до 2 МБ). Лежит отдельно от объявления, чтобы списки объявлений не тянули за собой байты.
    version нужна клиенту: сменилась картинка, значит надо скачать заново."""
    __tablename__ = "ad_images"
    ad_id: Mapped[int] = mapped_column(ForeignKey("ads.id"), primary_key=True)
    content_type: Mapped[str] = mapped_column(String(20))
    width: Mapped[int] = mapped_column(Integer)
    height: Mapped[int] = mapped_column(Integer)
    version: Mapped[str] = mapped_column(String(16))
    data: Mapped[bytes] = mapped_column(LargeBinary)
    created_at: Mapped[datetime] = mapped_column(DateTime, default=utcnow)


class AdStat(Base):
    """Сколько секунд объявление было на экранах блокировки клуба за день (по времени клуба).
    Считается по опросам ПК, а не со слов клиента: накрутить больше реального времени нельзя."""
    __tablename__ = "ad_stats"
    __table_args__ = (UniqueConstraint("club_id", "ad_id", "day", name="uq_ad_stats_club_ad_day"),)
    id: Mapped[int] = mapped_column(primary_key=True)
    club_id: Mapped[int] = mapped_column(ForeignKey("clubs.id"), index=True)
    ad_id: Mapped[int] = mapped_column(ForeignKey("ads.id"), index=True)
    day: Mapped[date] = mapped_column(Date)
    seconds: Mapped[int] = mapped_column(Integer, default=0, server_default="0")



class ClientRelease(Base):
    """Версия клиента (служба + оверлей) для автообновления. Общая для всех клубов платформы (club_id нет): её выпускает владелец платформы.
    Пакет (zip) подписан его закрытым ключом, клиент проверяет подпись встроенным открытым ключом: сервер подделать обновление не может.
    Лежит файл на диске сервера (CLUB_RELEASES_DIR), в базе только сведения. Раздаётся, пока active, и только тем ПК, до которых дошла очередь:
    club_ids (пусто: все клубы) и rollout_percent (доля ПК, по стабильному «номерку» ПК) позволяют сначала проверить версию на своих клубах."""
    __tablename__ = "client_releases"
    id: Mapped[int] = mapped_column(primary_key=True)
    version: Mapped[str] = mapped_column(String(20), unique=True)      # 1.4.0, до четырёх чисел
    notes: Mapped[str | None] = mapped_column(String(1000), nullable=True)
    sha256: Mapped[str] = mapped_column(String(64))
    size: Mapped[int] = mapped_column(Integer)
    signature: Mapped[str] = mapped_column(String(200))                # base64, ECDSA P-256 по «clubos-release|v1|версия|sha256|размер»
    file_name: Mapped[str | None] = mapped_column(String(80), nullable=True)   # пусто, пока пакет не загружен
    uploaded_at: Mapped[datetime | None] = mapped_column(DateTime, nullable=True)
    active: Mapped[bool] = mapped_column(Boolean, default=False, server_default="0")
    rollout_percent: Mapped[int] = mapped_column(Integer, default=0, server_default="0")
    club_ids: Mapped[str | None] = mapped_column(String(200), nullable=True)   # «1,5,9»; пусто: все клубы
    created_at: Mapped[datetime] = mapped_column(DateTime, default=utcnow)


class SyncJob(Base):
    """Синхронизация игр: файлы с эталонного ПК (source) копируются на другие ПК клуба по локальной сети, облачный сервер только координирует.
    paths это папки и файлы относительно игровой папки ПК (для игры Steam служба сама добавляет и её appmanifest). token нужен только участникам задания:
    им защищена раздача файлов внутри сети. manifest_* заполняет эталонный ПК, когда посчитал список файлов и хеши блоков; sha256 списка проверяют все получатели.
    running: идёт; done: все участники закончили (успешно или нет, это видно по строкам); cancelled: отменено или истёк срок."""
    __tablename__ = "sync_jobs"
    __table_args__ = (CheckConstraint("status in ('running', 'done', 'cancelled')", name="ck_sync_jobs_status"),)
    id: Mapped[int] = mapped_column(primary_key=True)
    club_id: Mapped[int] = mapped_column(ForeignKey("clubs.id"), index=True)
    source_pc_id: Mapped[int] = mapped_column(ForeignKey("pcs.id"))
    paths: Mapped[str] = mapped_column(Text)                                  # JSON-список
    delete_extra: Mapped[bool] = mapped_column(Boolean, default=False, server_default="0")   # убрать у получателей файлы, которых нет на эталоне (точное зеркало)
    status: Mapped[str] = mapped_column(String(10), default="running", server_default="running")
    token: Mapped[str] = mapped_column(String(64))
    created_by: Mapped[int | None] = mapped_column(ForeignKey("accounts.id"), nullable=True)
    created_at: Mapped[datetime] = mapped_column(DateTime, default=utcnow)
    finished_at: Mapped[datetime | None] = mapped_column(DateTime, nullable=True)
    manifest_sha256: Mapped[str | None] = mapped_column(String(64), nullable=True)
    manifest_files: Mapped[int | None] = mapped_column(Integer, nullable=True)
    manifest_bytes: Mapped[int | None] = mapped_column(BigInteger, nullable=True)


class SyncTarget(Base):
    """Участник задания синхронизации: эталонный ПК (role source) или получатель (target) и как у него идут дела.
    Статусы: pending (ещё не начал), scanning (эталон считает список файлов), waiting (получатель ждёт список или пока ПК занят гостем), syncing (копирует),
    ready (всё на месте, ПК раздаёт файлы другим), failed, cancelled. serve_url: адрес, по которому этот ПК раздаёт файлы внутри сети (только частные адреса)."""
    __tablename__ = "sync_targets"
    __table_args__ = (UniqueConstraint("job_id", "pc_id", name="uq_sync_targets_job_pc"),
                      CheckConstraint("status in ('pending', 'scanning', 'waiting', 'syncing', 'ready', 'failed', 'cancelled')", name="ck_sync_targets_status"),
                      CheckConstraint("role in ('source', 'target')", name="ck_sync_targets_role"))
    id: Mapped[int] = mapped_column(primary_key=True)
    job_id: Mapped[int] = mapped_column(ForeignKey("sync_jobs.id"), index=True)
    pc_id: Mapped[int] = mapped_column(ForeignKey("pcs.id"), index=True)
    role: Mapped[str] = mapped_column(String(6))
    status: Mapped[str] = mapped_column(String(10), default="pending", server_default="pending")
    bytes_done: Mapped[int] = mapped_column(BigInteger, default=0, server_default="0")          # сколько данных уже на месте и проверено
    bytes_total: Mapped[int] = mapped_column(BigInteger, default=0, server_default="0")
    bytes_fetched: Mapped[int] = mapped_column(BigInteger, default=0, server_default="0")       # сколько реально скачано по сети (остальное уже было на ПК)
    bytes_from_peers: Mapped[int] = mapped_column(BigInteger, default=0, server_default="0")    # из скачанного: сколько взято с других получателей, а не с эталона
    files_done: Mapped[int] = mapped_column(Integer, default=0, server_default="0")
    files_total: Mapped[int] = mapped_column(Integer, default=0, server_default="0")
    message: Mapped[str | None] = mapped_column(String(200), nullable=True)
    serve_url: Mapped[str | None] = mapped_column(String(80), nullable=True)
    updated_at: Mapped[datetime | None] = mapped_column(DateTime, nullable=True)
    finished_at: Mapped[datetime | None] = mapped_column(DateTime, nullable=True)
