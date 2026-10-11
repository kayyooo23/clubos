"""session extend

Revision ID: 0010
Revises: 0009
Create Date: 2026-10-07

Продление идущей сессии пакетом часов: sessions.extra_paid хранит, сколько уже списано сверх цены стартового тарифа
(купленные посреди игры пакеты и закрытая перед продлением доплата по часам).
"""
from typing import Sequence, Union

from alembic import op
import sqlalchemy as sa


revision: str = '0010'
down_revision: Union[str, Sequence[str], None] = '0009'
branch_labels: Union[str, Sequence[str], None] = None
depends_on: Union[str, Sequence[str], None] = None


def upgrade() -> None:
    op.add_column('sessions', sa.Column('extra_paid', sa.Integer(), server_default='0', nullable=False))


def downgrade() -> None:
    with op.batch_alter_table('sessions', schema=None) as batch_op:
        batch_op.drop_column('extra_paid')
