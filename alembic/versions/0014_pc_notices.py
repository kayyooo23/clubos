"""pc notices

Revision ID: 0014
Revises: 0013
Create Date: 2026-10-12

Сообщения администратора гостю на экран ПК.
"""
from typing import Sequence, Union

from alembic import op
import sqlalchemy as sa


revision: str = '0014'
down_revision: Union[str, Sequence[str], None] = '0013'
branch_labels: Union[str, Sequence[str], None] = None
depends_on: Union[str, Sequence[str], None] = None


def upgrade() -> None:
    op.create_table(
        'pc_notices',
        sa.Column('id', sa.Integer(), nullable=False),
        sa.Column('club_id', sa.Integer(), nullable=False),
        sa.Column('pc_id', sa.Integer(), nullable=False),
        sa.Column('text', sa.String(length=300), nullable=False),
        sa.Column('created_by', sa.Integer(), nullable=True),
        sa.Column('created_at', sa.DateTime(), nullable=False),
        sa.Column('delivered_at', sa.DateTime(), nullable=True),
        sa.ForeignKeyConstraint(['club_id'], ['clubs.id']),
        sa.ForeignKeyConstraint(['created_by'], ['accounts.id']),
        sa.ForeignKeyConstraint(['pc_id'], ['pcs.id']),
        sa.PrimaryKeyConstraint('id'),
    )
    op.create_index(op.f('ix_pc_notices_club_id'), 'pc_notices', ['club_id'], unique=False)
    op.create_index(op.f('ix_pc_notices_pc_id'), 'pc_notices', ['pc_id'], unique=False)


def downgrade() -> None:
    op.drop_index(op.f('ix_pc_notices_pc_id'), table_name='pc_notices')
    op.drop_index(op.f('ix_pc_notices_club_id'), table_name='pc_notices')
    op.drop_table('pc_notices')
