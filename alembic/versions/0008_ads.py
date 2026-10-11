"""ads

Revision ID: 0008
Revises: 0007
Create Date: 2026-10-07

Реклама партнёров на экране блокировки (бегущая строка). Объявления заводит владелец платформы, они общие для всех клубов.
Клуб сам включает показ (clubs.ads_enabled) и получает долю дохода (clubs.ads_club_share, проценты). Время показа считается по
опросам ПК и складывается в ad_stats по клубам, объявлениям и дням.
"""
from typing import Sequence, Union

from alembic import op
import sqlalchemy as sa


revision: str = '0008'
down_revision: Union[str, Sequence[str], None] = '0007'
branch_labels: Union[str, Sequence[str], None] = None
depends_on: Union[str, Sequence[str], None] = None


def upgrade() -> None:
    op.add_column('clubs', sa.Column('ads_enabled', sa.Boolean(), server_default='0', nullable=False))
    op.add_column('clubs', sa.Column('ads_club_share', sa.Integer(), server_default='30', nullable=False))
    op.add_column('pcs', sa.Column('ads_seen_at', sa.DateTime(), nullable=True))

    op.create_table(
        'ads',
        sa.Column('id', sa.Integer(), nullable=False),
        sa.Column('text', sa.String(length=200), nullable=False),
        sa.Column('advertiser', sa.String(length=120), nullable=True),
        sa.Column('erid', sa.String(length=80), nullable=True),
        sa.Column('active', sa.Boolean(), server_default='1', nullable=False),
        sa.Column('starts_at', sa.DateTime(), nullable=True),
        sa.Column('ends_at', sa.DateTime(), nullable=True),
        sa.Column('sort_order', sa.Integer(), server_default='100', nullable=False),
        sa.Column('created_at', sa.DateTime(), nullable=False),
        sa.PrimaryKeyConstraint('id'),
    )
    op.create_table(
        'ad_stats',
        sa.Column('id', sa.Integer(), nullable=False),
        sa.Column('club_id', sa.Integer(), nullable=False),
        sa.Column('ad_id', sa.Integer(), nullable=False),
        sa.Column('day', sa.Date(), nullable=False),
        sa.Column('seconds', sa.Integer(), server_default='0', nullable=False),
        sa.ForeignKeyConstraint(['ad_id'], ['ads.id']),
        sa.ForeignKeyConstraint(['club_id'], ['clubs.id']),
        sa.PrimaryKeyConstraint('id'),
        sa.UniqueConstraint('club_id', 'ad_id', 'day', name='uq_ad_stats_club_ad_day'),
    )
    op.create_index(op.f('ix_ad_stats_ad_id'), 'ad_stats', ['ad_id'], unique=False)
    op.create_index(op.f('ix_ad_stats_club_id'), 'ad_stats', ['club_id'], unique=False)


def downgrade() -> None:
    op.drop_index(op.f('ix_ad_stats_club_id'), table_name='ad_stats')
    op.drop_index(op.f('ix_ad_stats_ad_id'), table_name='ad_stats')
    op.drop_table('ad_stats')
    op.drop_table('ads')
    # удаление колонок в SQLite пересоздаёт таблицы clubs и pcs; частичных индексов на них нет, поэтому ничего восстанавливать не нужно
    with op.batch_alter_table('pcs', schema=None) as batch_op:
        batch_op.drop_column('ads_seen_at')
    with op.batch_alter_table('clubs', schema=None) as batch_op:
        batch_op.drop_column('ads_club_share')
        batch_op.drop_column('ads_enabled')
