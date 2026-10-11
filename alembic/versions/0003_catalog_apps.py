"""catalog apps

Revision ID: 0003
Revises: 0002
Create Date: 2026-10-06

Каталог игр и программ для меню гостя (на каждый клуб свой список).
"""
from typing import Sequence, Union

from alembic import op
import sqlalchemy as sa


revision: str = '0003'
down_revision: Union[str, Sequence[str], None] = '0002'
branch_labels: Union[str, Sequence[str], None] = None
depends_on: Union[str, Sequence[str], None] = None


def upgrade() -> None:
    op.create_table(
        'catalog_apps',
        sa.Column('id', sa.Integer(), nullable=False),
        sa.Column('club_id', sa.Integer(), nullable=False),
        sa.Column('title', sa.String(length=80), nullable=False),
        sa.Column('tab', sa.String(length=10), nullable=False),
        sa.Column('category', sa.String(length=40), nullable=False),
        sa.Column('age_rating', sa.Integer(), nullable=True),
        sa.Column('launch_kind', sa.String(length=10), nullable=False),
        sa.Column('launch_target', sa.String(length=260), nullable=False),
        sa.Column('sort_order', sa.Integer(), server_default='100', nullable=False),
        sa.Column('active', sa.Boolean(), server_default='1', nullable=False),
        sa.Column('created_at', sa.DateTime(), nullable=False),
        sa.CheckConstraint("tab in ('games', 'apps')", name='ck_catalog_apps_tab'),
        sa.CheckConstraint("launch_kind in ('steam', 'exe')", name='ck_catalog_apps_launch_kind'),
        sa.ForeignKeyConstraint(['club_id'], ['clubs.id']),
        sa.PrimaryKeyConstraint('id'),
    )
    op.create_index(op.f('ix_catalog_apps_club_id'), 'catalog_apps', ['club_id'], unique=False)


def downgrade() -> None:
    op.drop_index(op.f('ix_catalog_apps_club_id'), table_name='catalog_apps')
    op.drop_table('catalog_apps')
