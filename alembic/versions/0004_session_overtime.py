"""session overtime

Revision ID: 0004
Revises: 0003
Create Date: 2026-10-06

Доплата по часам после окончания пакета или ночи: в сессии запоминается конец тарифа (base_end)
и почасовая ставка (overtime_price). У старых сессий обе колонки пустые, они ведут себя как раньше.
"""
from typing import Sequence, Union

from alembic import op
import sqlalchemy as sa


revision: str = '0004'
down_revision: Union[str, Sequence[str], None] = '0003'
branch_labels: Union[str, Sequence[str], None] = None
depends_on: Union[str, Sequence[str], None] = None


def upgrade() -> None:
    # обычное добавление колонок: таблицу в SQLite пересоздавать не нужно, частичные индексы sessions остаются на месте
    op.add_column('sessions', sa.Column('base_end', sa.DateTime(), nullable=True))
    op.add_column('sessions', sa.Column('overtime_price', sa.Integer(), nullable=True))


def downgrade() -> None:
    # в SQLite удаление колонки пересоздаёт таблицу и теряет WHERE у частичных индексов: снимаем их и создаём заново
    active = sa.text('ended_at IS NULL')
    op.drop_index('uq_sessions_one_active_per_pc', table_name='sessions')
    op.drop_index('uq_sessions_one_active_per_account', table_name='sessions')
    with op.batch_alter_table('sessions', schema=None) as batch_op:
        batch_op.drop_column('overtime_price')
        batch_op.drop_column('base_end')
    op.create_index('uq_sessions_one_active_per_pc', 'sessions', ['pc_id'], unique=True,
                    sqlite_where=active, postgresql_where=active)
    op.create_index('uq_sessions_one_active_per_account', 'sessions', ['account_id'], unique=True,
                    sqlite_where=active, postgresql_where=active)
