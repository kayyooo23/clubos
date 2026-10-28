"""client releases and pc client version

Revision ID: 0017
Revises: 0016
Create Date: 2026-10-13

Автообновление клиента: версии клиента (client_releases) и поля pcs о версии на ПК и итоге последнего обновления.
"""
from typing import Sequence, Union

from alembic import op
import sqlalchemy as sa


revision: str = '0017'
down_revision: Union[str, Sequence[str], None] = '0016'
branch_labels: Union[str, Sequence[str], None] = None
depends_on: Union[str, Sequence[str], None] = None


def upgrade() -> None:
    op.create_table(
        'client_releases',
        sa.Column('id', sa.Integer(), nullable=False),
        sa.Column('version', sa.String(length=20), nullable=False),
        sa.Column('notes', sa.String(length=1000), nullable=True),
        sa.Column('sha256', sa.String(length=64), nullable=False),
        sa.Column('size', sa.Integer(), nullable=False),
        sa.Column('signature', sa.String(length=200), nullable=False),
        sa.Column('file_name', sa.String(length=80), nullable=True),
        sa.Column('uploaded_at', sa.DateTime(), nullable=True),
        sa.Column('active', sa.Boolean(), server_default='0', nullable=False),
        sa.Column('rollout_percent', sa.Integer(), server_default='0', nullable=False),
        sa.Column('club_ids', sa.String(length=200), nullable=True),
        sa.Column('created_at', sa.DateTime(), nullable=False),
        sa.PrimaryKeyConstraint('id'),
        sa.UniqueConstraint('version'),
    )
    with op.batch_alter_table('pcs', schema=None) as batch_op:
        batch_op.add_column(sa.Column('client_version', sa.String(length=20), nullable=True))
        batch_op.add_column(sa.Column('client_version_at', sa.DateTime(), nullable=True))
        batch_op.add_column(sa.Column('update_status', sa.String(length=12), nullable=True))
        batch_op.add_column(sa.Column('update_message', sa.String(length=200), nullable=True))
        batch_op.add_column(sa.Column('update_at', sa.DateTime(), nullable=True))


def downgrade() -> None:
    with op.batch_alter_table('pcs', schema=None) as batch_op:
        batch_op.drop_column('update_at')
        batch_op.drop_column('update_message')
        batch_op.drop_column('update_status')
        batch_op.drop_column('client_version_at')
        batch_op.drop_column('client_version')
    op.drop_table('client_releases')
