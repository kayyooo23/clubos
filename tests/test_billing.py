from datetime import datetime

from app.models import Session, Tariff
from app.services import billing


def test_hourly_cost_per_minute():
    t = Tariff(kind="hourly", price=120)
    s = Session(tariff=t, started_at=datetime(2026, 1, 1, 12, 0))
    assert billing.compute_cost(s, datetime(2026, 1, 1, 12, 30)) == 60
    assert billing.compute_cost(s, datetime(2026, 1, 1, 12, 1)) == 2
    assert billing.compute_cost(s, datetime(2026, 1, 1, 13, 0)) == 120


def test_hourly_cost_never_exceeds_the_balance_that_set_the_end():
    """Конец почасовой сессии выводится из баланса; на этом самом конце стоимость не должна оказаться больше баланса
    (раньше из-за float выходило 32 ₽ при балансе 31 ₽ и цене 25 ₽/ч). Перебираем много сочетаний."""
    start = datetime(2026, 1, 1, 12, 0, 0, 123456)
    for price in range(1, 300, 7):
        t = Tariff(kind="hourly", price=price)
        for balance in range(1, 1500, 11):
            end = billing.hourly_limit_end(t, start, balance)
            s = Session(tariff=t, started_at=start, planned_end=end, ended_at=end)
            assert billing.compute_cost(s, end) <= balance, (price, balance)
    t = Tariff(kind="hourly", price=25)  # конкретный случай из жизни
    end = billing.hourly_limit_end(t, start, 31)
    assert billing.compute_cost(Session(tariff=t, started_at=start, planned_end=end, ended_at=end), end) == 31


def test_night_window_over_midnight():
    t = Tariff(kind="night", price=400, night_start=23, night_end=8)
    # 23:30 МСК = 20:30 UTC
    assert billing.in_night_window(t, datetime(2026, 1, 1, 20, 30), 3)
    assert not billing.in_night_window(t, datetime(2026, 1, 1, 12, 0), 3)  # 15:00 МСК
    # конец ночи: 8:00 МСК = 05:00 UTC следующего дня
    assert billing.night_end_time(t, datetime(2026, 1, 1, 20, 30), 3) == datetime(2026, 1, 2, 5, 0)


def test_night_end_after_midnight_is_same_local_day():
    t = Tariff(kind="night", price=400, night_start=23, night_end=8)
    # 02:00 МСК 2 января = 23:00 UTC 1 января; ночь кончается в 08:00 МСК того же дня = 05:00 UTC
    assert billing.night_end_time(t, datetime(2026, 1, 1, 23, 0), 3) == datetime(2026, 1, 2, 5, 0)


def test_night_window_depends_on_club_timezone():
    t = Tariff(kind="night", price=400, night_start=23, night_end=8)
    now = datetime(2026, 1, 1, 20, 30)  # один и тот же момент
    assert billing.in_night_window(t, now, 3)       # у клуба в Москве 23:30 — ночь
    assert not billing.in_night_window(t, now, 0)   # у клуба на UTC 20:30 — ещё нет
    assert billing.in_night_window(t, now, 5)       # у клуба на UTC+5 01:30 — ночь


def test_planned_end():
    start = datetime(2026, 1, 1, 12, 0)
    pkg = Tariff(kind="package", price=250, package_minutes=180)
    assert billing.compute_planned_end(pkg, start, 3) == datetime(2026, 1, 1, 15, 0)
    assert billing.compute_planned_end(Tariff(kind="hourly", price=100), start, 3) is None


def test_package_cost_is_fixed_even_if_stopped_early():
    t = Tariff(kind="package", price=250, package_minutes=180)
    s = Session(tariff=t, started_at=datetime(2026, 1, 1, 12, 0), planned_end=datetime(2026, 1, 1, 15, 0))
    assert billing.compute_cost(s, datetime(2026, 1, 1, 12, 5)) == 250
