"""Бар клуба и вызов администратора. Всё строго в пределах клуба (club_id берётся из токена или ключа ПК).
Деньги: заказ списывается с баланса сразу (wallet, shop_purchase), отмена до выдачи возвращает всё (shop_refund).
Статус меняется условным UPDATE: два одновременных нажатия «отменить» не вернут деньги дважды."""
from datetime import datetime

from pydantic import ValidationError
from sqlalchemy import func, select, update
from sqlalchemy.exc import IntegrityError
from sqlalchemy.orm import Session as DB

from ..errors import ServiceError
from ..models import Account, Call, Order, OrderItem, PC, Product
from ..schemas import OrderIn, ProductIn, ProductPatch
from . import wallet

MAX_PRODUCTS_PER_CLUB = 300
MAX_OPEN_ORDERS_PER_GUEST = 5          # защита от случайного «заказал 20 раз» и от спама бару
OPEN_STATUSES = ("new", "accepted")
# какие переходы разрешены сотруднику
TRANSITIONS = {"new": {"accepted", "done", "cancelled"}, "accepted": {"done", "cancelled"}}


# ---------- товары ----------
def product_out(p: Product, *, admin: bool = False) -> dict:
    out = {"id": p.id, "name": p.name, "category": p.category, "price": p.price}
    if admin:
        out.update(sort_order=p.sort_order, active=p.active)
    return out


def list_products(db: DB, club_id: int, *, active_only: bool) -> list[Product]:
    q = select(Product).where(Product.club_id == club_id)
    if active_only:
        q = q.where(Product.active)
    return list(db.scalars(q.order_by(Product.sort_order, Product.id)))


def get_product(db: DB, club_id: int, product_id: int) -> Product:
    p = db.scalar(select(Product).where(Product.id == product_id, Product.club_id == club_id))
    if not p:
        raise ServiceError(404, "Товар не найден")
    return p


def _first_error(e: ValidationError) -> ServiceError:
    err = e.errors()[0]
    field = ".".join(str(p) for p in err["loc"] if p != "__root__")
    msg = str(err["msg"]).removeprefix("Value error, ")
    return ServiceError(422, f"{field}: {msg}" if field else msg)


def create_product(db: DB, club_id: int, data: ProductIn, now: datetime) -> Product:
    count = db.scalar(select(func.count(Product.id)).where(Product.club_id == club_id)) or 0
    if count >= MAX_PRODUCTS_PER_CLUB:
        raise ServiceError(409, f"В меню бара уже {MAX_PRODUCTS_PER_CLUB} товаров, это предел")
    p = Product(club_id=club_id, created_at=now, **data.model_dump())
    db.add(p)
    db.flush()
    return p


def update_product(db: DB, club_id: int, product_id: int, patch: ProductPatch) -> Product:
    p = get_product(db, club_id, product_id)
    merged = {"name": p.name, "category": p.category, "price": p.price, "sort_order": p.sort_order, "active": p.active}
    merged.update(patch.model_dump(exclude_unset=True))
    try:
        checked = ProductIn.model_validate(merged)
    except ValidationError as e:
        raise _first_error(e)
    for field, value in checked.model_dump().items():
        setattr(p, field, value)
    return p


def delete_product(db: DB, club_id: int, product_id: int) -> None:
    p = get_product(db, club_id, product_id)
    if db.scalar(select(func.count(OrderItem.id)).where(OrderItem.product_id == p.id)):
        raise ServiceError(409, "Этот товар уже заказывали. Скройте его из меню, удалять нельзя")
    db.delete(p)


# ---------- заказы ----------
def order_out(o: Order, *, pc_name: str | None = None, phone: str | None = None) -> dict:
    out = {"id": o.id, "pc_id": o.pc_id, "total": o.total, "status": o.status, "comment": o.comment,
           "created_at": o.created_at, "updated_at": o.updated_at,
           "items": [{"name": i.name, "price": i.price, "qty": i.qty} for i in o.items]}
    if pc_name is not None or phone is not None:
        out.update(pc_name=pc_name, guest_phone=phone)
    return out


def create_order(db: DB, club_id: int, pc: PC, account: Account, data: OrderIn, now: datetime) -> Order:
    wanted: dict[int, int] = {}
    for item in data.items:           # одинаковые товары складываем в одну строку
        wanted[item.product_id] = wanted.get(item.product_id, 0) + item.qty
    if any(q > 20 for q in wanted.values()):
        raise ServiceError(422, "Не больше 20 штук одного товара в заказе")

    products = {p.id: p for p in db.scalars(select(Product).where(Product.club_id == club_id, Product.id.in_(wanted)))}
    for pid in wanted:
        if pid not in products or not products[pid].active:
            raise ServiceError(404, "Одного из товаров уже нет в меню. Обновите список")

    open_orders = db.scalar(select(func.count(Order.id)).where(
        Order.club_id == club_id, Order.account_id == account.id, Order.status.in_(OPEN_STATUSES))) or 0
    if open_orders >= MAX_OPEN_ORDERS_PER_GUEST:
        raise ServiceError(429, "У вас уже несколько заказов в работе. Дождитесь, пока их принесут")

    total = sum(products[pid].price * qty for pid, qty in wanted.items())
    db.refresh(account)
    if account.balance < total:
        raise ServiceError(402, f"Не хватает {total - account.balance} ₽ на балансе")

    order = Order(club_id=club_id, pc_id=pc.id, account_id=account.id, total=total, status="new",
                  comment=data.comment, created_at=now, updated_at=now)
    for pid, qty in wanted.items():
        p = products[pid]
        order.items.append(OrderItem(product_id=p.id, name=p.name, price=p.price, qty=qty))
    db.add(order)
    db.flush()  # нужен номер заказа для записи в журнал денег
    wallet.apply(db, account, -total, "shop_purchase", now, comment=f"Заказ №{order.id}")
    db.commit()
    return order


def get_order(db: DB, club_id: int, order_id: int, *, account_id: int | None = None) -> Order:
    q = select(Order).where(Order.id == order_id, Order.club_id == club_id)
    if account_id is not None:
        q = q.where(Order.account_id == account_id)
    o = db.scalar(q)
    if not o:
        raise ServiceError(404, "Заказ не найден")
    return o


def _move(db: DB, o: Order, allowed_from: tuple[str, ...], new_status: str, now: datetime, staff_id: int | None) -> bool:
    """Условная смена статуса: срабатывает только если заказ ещё в одном из allowed_from. Возвращает, получилось ли."""
    res = db.execute(update(Order).where(Order.id == o.id, Order.club_id == o.club_id, Order.status.in_(allowed_from))
                     .values(status=new_status, updated_at=now, handled_by=staff_id))
    return res.rowcount == 1


def _refund(db: DB, o: Order, now: datetime) -> None:
    account = db.get(Account, o.account_id)
    wallet.apply(db, account, o.total, "shop_refund", now, comment=f"Возврат за заказ №{o.id}")


def cancel_by_guest(db: DB, club_id: int, order_id: int, account_id: int, now: datetime) -> Order:
    """Гость может отменить свой заказ, пока его никто не принял."""
    o = get_order(db, club_id, order_id, account_id=account_id)
    if not _move(db, o, ("new",), "cancelled", now, None):
        db.rollback()
        raise ServiceError(409, "Заказ уже принят в работу: отменить его может только сотрудник")
    _refund(db, o, now)
    db.commit()
    db.refresh(o)
    return o


def set_status(db: DB, club_id: int, order_id: int, status: str, staff: Account, now: datetime) -> Order:
    """Сотрудник двигает заказ: принят → выдан, либо отмена (с возвратом денег гостю)."""
    o = get_order(db, club_id, order_id)
    if status not in TRANSITIONS.get(o.status, set()):
        raise ServiceError(409, "Этот заказ уже закрыт" if o.status in ("done", "cancelled") else "Так заказ перевести нельзя")
    sources = tuple(s for s, targets in TRANSITIONS.items() if status in targets)
    if not _move(db, o, sources, status, now, staff.id):
        db.rollback()
        raise ServiceError(409, "Заказ уже изменили, обновите список")
    if status == "cancelled":
        _refund(db, o, now)
    db.commit()
    db.refresh(o)
    return o


def list_orders(db: DB, club_id: int, *, open_only: bool, limit: int) -> list[dict]:
    q = (select(Order, PC.name, Account.phone).join(PC, PC.id == Order.pc_id).join(Account, Account.id == Order.account_id)
         .where(Order.club_id == club_id))
    if open_only:
        q = q.where(Order.status.in_(OPEN_STATUSES)).order_by(Order.id)          # старые заказы сверху: их ждут дольше всех
    else:
        q = q.order_by(Order.id.desc())
    return [order_out(o, pc_name=pc_name, phone=phone) for o, pc_name, phone in db.execute(q.limit(min(max(limit, 1), 300)))]


def guest_orders(db: DB, club_id: int, account_id: int, limit: int = 20) -> list[Order]:
    return list(db.scalars(select(Order).where(Order.club_id == club_id, Order.account_id == account_id)
                           .order_by(Order.id.desc()).limit(limit)))


# ---------- вызовы администратора ----------
def call_out(c: Call, *, pc_name: str | None = None, phone: str | None = None) -> dict:
    out = {"id": c.id, "pc_id": c.pc_id, "text": c.text, "status": c.status,
           "created_at": c.created_at, "handled_at": c.handled_at}
    if pc_name is not None or phone is not None:
        out.update(pc_name=pc_name, guest_phone=phone)
    return out


def open_call(db: DB, club_id: int, pc_id: int) -> Call | None:
    return db.scalar(select(Call).where(Call.club_id == club_id, Call.pc_id == pc_id, Call.status == "open"))


def create_call(db: DB, club_id: int, pc: PC, account: Account, text: str | None, now: datetime) -> tuple[Call, bool]:
    """Возвращает (вызов, был_уже_открыт). Повторное нажатие на том же ПК новый вызов не создаёт."""
    existing = open_call(db, club_id, pc.id)
    if existing:
        return existing, True
    call = Call(club_id=club_id, pc_id=pc.id, account_id=account.id, text=text, status="open", created_at=now)
    db.add(call)
    try:
        db.commit()
    except IntegrityError:  # два нажатия одновременно: выиграло другое
        db.rollback()
        return open_call(db, club_id, pc.id), True
    return call, False


def list_calls(db: DB, club_id: int, *, open_only: bool, limit: int) -> list[dict]:
    q = (select(Call, PC.name, Account.phone).join(PC, PC.id == Call.pc_id).outerjoin(Account, Account.id == Call.account_id)
         .where(Call.club_id == club_id))
    q = q.where(Call.status == "open").order_by(Call.id) if open_only else q.order_by(Call.id.desc())
    return [call_out(c, pc_name=pc_name, phone=phone) for c, pc_name, phone in db.execute(q.limit(min(max(limit, 1), 300)))]


def finish_call(db: DB, club_id: int, call_id: int, staff: Account, now: datetime) -> Call:
    c = db.scalar(select(Call).where(Call.id == call_id, Call.club_id == club_id))
    if not c:
        raise ServiceError(404, "Вызов не найден")
    if c.status == "open":
        c.status, c.handled_at, c.handled_by = "done", now, staff.id
        db.commit()
    return c


def inbox(db: DB, club_id: int) -> dict:
    """Сколько всего ждёт сотрудника: для значка в шапке админки."""
    new_orders = db.scalar(select(func.count(Order.id)).where(Order.club_id == club_id, Order.status == "new")) or 0
    open_orders = db.scalar(select(func.count(Order.id)).where(Order.club_id == club_id, Order.status.in_(OPEN_STATUSES))) or 0
    open_calls = db.scalar(select(func.count(Call.id)).where(Call.club_id == club_id, Call.status == "open")) or 0
    return {"orders_new": new_orders, "orders_open": open_orders, "calls_open": open_calls}


# Стартовое меню бара: ходовые напитки и еда. Цены примерные, их правят в админке (раздел «Бар»).
STARTER_PRODUCTS = [
    ("Кола 0,5 л", "Напитки", 90), ("Вода 0,5 л", "Напитки", 50), ("Чай чёрный", "Напитки", 60), ("Кофе", "Напитки", 100),
    ("Энергетик", "Напитки", 150), ("Бургер", "Еда", 280), ("Картошка фри", "Еда", 170), ("Сэндвич", "Еда", 190),
    ("Чипсы", "Снеки", 120), ("Шоколадный батончик", "Снеки", 80),
]


def seed_starter_products(db: DB, club_id: int, now: datetime) -> int:
    """Кладёт стартовое меню в пустой бар клуба. Если там уже есть товары, ничего не делает."""
    if db.scalar(select(func.count(Product.id)).where(Product.club_id == club_id)):
        return 0
    for i, (name, category, price) in enumerate(STARTER_PRODUCTS):
        db.add(Product(club_id=club_id, name=name, category=category, price=price, sort_order=10 * (i + 1), created_at=now))
    return len(STARTER_PRODUCTS)

