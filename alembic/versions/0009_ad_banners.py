"""ad banners

Revision ID: 0009
Revises: 0008
Create Date: 2026-10-07

Реклама стала баннерной: у объявления есть картинка (ad_images) и время показа в ротации (ads.duration_seconds).
Поле ads.text теперь внутреннее название для владельца платформы, гостю оно не показывается.
"""
from typing import Sequence, Union

from alembic import op
import sqlalchemy as sa


revision: str = '0009'
down_revision: Union[str, Sequence[str], None] = '0008'
branch_labels: Union[str, Sequence[str], None] = None
depends_on: Union[str, Sequence[str], None] = None


def upgrade() -> None:
    op.add_column('ads', sa.Column('duration_seconds', sa.Integer(), server_default='10', nullable=False))
    op.create_table(
        'ad_images',
        sa.Column('ad_id', sa.Integer(), nullable=False),
        sa.Column('content_type', sa.String(length=20), nullable=False),
        sa.Column('width', sa.Integer(), nullable=False),
        sa.Column('height', sa.Integer(), nullable=False),
        sa.Column('version', sa.String(length=16), nullable=False),
        sa.Column('data', sa.LargeBinary(), nullable=False),
        sa.Column('created_at', sa.DateTime(), nullable=False),
        sa.ForeignKeyConstraint(['ad_id'], ['ads.id']),
        sa.PrimaryKeyConstraint('ad_id'),
    )


def downgrade() -> None:
    op.drop_table('ad_images')
    with op.batch_alter_table('ads', schema=None) as batch_op:
        batch_op.drop_column('duration_seconds')
