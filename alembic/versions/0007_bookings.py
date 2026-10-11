"""bookings

Revision ID: 0007
Revises: 0006
Create Date: 2026-10-06

Бронирование ПК: на какой ПК, на чей номер, на какое время.
"""
from typing import Sequence, Union

from alembic import op
import sqlalchemy as sa


revision: str = '0007'
down_revision: Union[str, Sequence[str], None] = '0006'
branch_labels: Union[str, Sequence[str], None] = None
depends_on: Union[str, Sequence[str], None] = None


def upgrade() -> None:
    op.create_table(
        'bookings',
        sa.Column('id', sa.Integer(), nullable=False),
        sa.Column('club_id', sa.Integer(), nullable=False),
        sa.Column('pc_id', sa.Integer(), nullable=False),
        sa.Column('phone', sa.String(length=20), nullable=False),
        sa.Column('name', sa.String(length=80), nullable=True),
        sa.Column('comment', sa.String(length=200), nullable=True),
        sa.Column('starts_at', sa.DateTime(), nullable=False),
        sa.Column('ends_at', sa.DateTime(), nullable=False),
        sa.Column('status', sa.String(length=10), nullable=False),
        sa.Column('created_by', sa.Integer(), nullable=True),
        sa.Column('created_at', sa.DateTime(), nullable=False),
        sa.CheckConstraint("status in ('active', 'cancelled', 'used')", name='ck_bookings_status'),
        sa.CheckConstraint('ends_at > starts_at', name='ck_bookings_period'),
        sa.ForeignKeyConstraint(['club_id'], ['clubs.id']),
        sa.ForeignKeyConstraint(['created_by'], ['accounts.id']),
        sa.ForeignKeyConstraint(['pc_id'], ['pcs.id']),
        sa.PrimaryKeyConstraint('id'),
    )
    op.create_index(op.f('ix_bookings_club_id'), 'bookings', ['club_id'], unique=False)
    op.create_index(op.f('ix_bookings_pc_id'), 'bookings', ['pc_id'], unique=False)


def downgrade() -> None:
    op.drop_index(op.f('ix_bookings_pc_id'), table_name='bookings')
    op.drop_index(op.f('ix_bookings_club_id'), table_name='bookings')
    op.drop_table('bookings')
