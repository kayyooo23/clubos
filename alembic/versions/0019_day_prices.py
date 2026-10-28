"""day prices

Revision ID: 0019
Revises: 0018
Create Date: 2026-10-19

Цены тарифов по дням недели (tariffs.day_prices) и цена, зафиксированная в сессии при старте (sessions.price).
"""
from typing import Sequence, Union

from alembic import op
import sqlalchemy as sa


revision: str = '0019'
down_revision: Union[str, Sequence[str], None] = '0018'
branch_labels: Union[str, Sequence[str], None] = None
depends_on: Union[str, Sequence[str], None] = None


def upgrade() -> None:
    with op.batch_alter_table('tariffs', schema=None) as batch_op:
        batch_op.add_column(sa.Column('day_prices', sa.Text(), nullable=True))
    with op.batch_alter_table('sessions', schema=None) as batch_op:
        batch_op.add_column(sa.Column('price', sa.Integer(), nullable=True))


def downgrade() -> None:
    with op.batch_alter_table('sessions', schema=None) as batch_op:
        batch_op.drop_column('price')
    with op.batch_alter_table('tariffs', schema=None) as batch_op:
        batch_op.drop_column('day_prices')
