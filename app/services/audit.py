from datetime import datetime

from sqlalchemy.orm import Session as DB

from ..models import Account, AuditLog


def record(db: DB, club_id: int, action: str, now: datetime, *, actor: Account | None = None,
           pc_id: int | None = None, details: str | None = None, ip: str | None = None) -> None:
    """Добавляет запись в журнал действий. Коммитит вызывающий — запись попадает в ту же транзакцию, что и действие.
    В details не пишем телефоны и пароли, только id и суммы."""
    db.add(AuditLog(club_id=club_id, actor_account_id=actor.id if actor else None, pc_id=pc_id,
                    action=action, details=details, ip=ip, created_at=now))
