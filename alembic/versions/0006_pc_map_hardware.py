"""pc map hardware

Revision ID: 0006
Revises: 0005
Create Date: 2026-10-06

Карта клуба: у ПК появляются зал, клетка на карте (pos_x, pos_y), заметка сотрудника, флаг «на обслуживании»
и характеристики железа от клиента (hw_json) с отметкой, когда конфигурация изменилась.
"""
from typing import Sequence, Union

from alembic import op
import sqlalchemy as sa


revision: str = '0006'
down_revision: Union[str, Sequence[str], None] = '0005'
branch_labels: Union[str, Sequence[str], None] = None
depends_on: Union[str, Sequence[str], None] = None

_COLUMNS = ('hw_changed_at', 'hw_updated_at', 'hw_json', 'maintenance', 'note', 'pos_y', 'pos_x', 'zone')


def upgrade() -> None:
    # обычное добавление колонок: таблицу pcs в SQLite пересоздавать не нужно
    op.add_column('pcs', sa.Column('zone', sa.String(length=40), server_default='Общий зал', nullable=False))
    op.add_column('pcs', sa.Column('pos_x', sa.Integer(), nullable=True))
    op.add_column('pcs', sa.Column('pos_y', sa.Integer(), nullable=True))
    op.add_column('pcs', sa.Column('note', sa.String(length=200), nullable=True))
    op.add_column('pcs', sa.Column('maintenance', sa.Boolean(), server_default='0', nullable=False))
    op.add_column('pcs', sa.Column('hw_json', sa.Text(), nullable=True))
    op.add_column('pcs', sa.Column('hw_updated_at', sa.DateTime(), nullable=True))
    op.add_column('pcs', sa.Column('hw_changed_at', sa.DateTime(), nullable=True))


def downgrade() -> None:
    with op.batch_alter_table('pcs', schema=None) as batch_op:
        for name in _COLUMNS:
            batch_op.drop_column(name)
