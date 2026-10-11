"""app covers

Revision ID: 0011
Revises: 0010
Create Date: 2026-10-07

Обложки игр в меню гостя: постер (PNG или JPEG) на каждую игру каталога. source: steam (подтянута из Steam) или manual (загружена вручную).
"""
from typing import Sequence, Union

from alembic import op
import sqlalchemy as sa


revision: str = '0011'
down_revision: Union[str, Sequence[str], None] = '0010'
branch_labels: Union[str, Sequence[str], None] = None
depends_on: Union[str, Sequence[str], None] = None


def upgrade() -> None:
    op.create_table(
        'app_covers',
        sa.Column('app_id', sa.Integer(), nullable=False),
        sa.Column('content_type', sa.String(length=20), nullable=False),
        sa.Column('width', sa.Integer(), nullable=False),
        sa.Column('height', sa.Integer(), nullable=False),
        sa.Column('version', sa.String(length=16), nullable=False),
        sa.Column('source', sa.String(length=10), nullable=False),
        sa.Column('data', sa.LargeBinary(), nullable=False),
        sa.Column('created_at', sa.DateTime(), nullable=False),
        sa.CheckConstraint("source in ('steam', 'manual')", name='ck_app_covers_source'),
        sa.ForeignKeyConstraint(['app_id'], ['catalog_apps.id']),
        sa.PrimaryKeyConstraint('app_id'),
    )


def downgrade() -> None:
    op.drop_table('app_covers')
