"""game sync

Revision ID: 0018
Revises: 0017
Create Date: 2026-10-13

Синхронизация игр между ПК клуба: список игр на ПК (pcs.games_*), задания (sync_jobs) и участники заданий (sync_targets).
"""
from typing import Sequence, Union

from alembic import op
import sqlalchemy as sa


revision: str = '0018'
down_revision: Union[str, Sequence[str], None] = '0017'
branch_labels: Union[str, Sequence[str], None] = None
depends_on: Union[str, Sequence[str], None] = None


def upgrade() -> None:
    with op.batch_alter_table('pcs', schema=None) as batch_op:
        batch_op.add_column(sa.Column('games_json', sa.Text(), nullable=True))
        batch_op.add_column(sa.Column('games_at', sa.DateTime(), nullable=True))
        batch_op.add_column(sa.Column('games_wanted_at', sa.DateTime(), nullable=True))

    op.create_table(
        'sync_jobs',
        sa.Column('id', sa.Integer(), nullable=False),
        sa.Column('club_id', sa.Integer(), nullable=False),
        sa.Column('source_pc_id', sa.Integer(), nullable=False),
        sa.Column('paths', sa.Text(), nullable=False),
        sa.Column('delete_extra', sa.Boolean(), server_default='0', nullable=False),
        sa.Column('status', sa.String(length=10), server_default='running', nullable=False),
        sa.Column('token', sa.String(length=64), nullable=False),
        sa.Column('created_by', sa.Integer(), nullable=True),
        sa.Column('created_at', sa.DateTime(), nullable=False),
        sa.Column('finished_at', sa.DateTime(), nullable=True),
        sa.Column('manifest_sha256', sa.String(length=64), nullable=True),
        sa.Column('manifest_files', sa.Integer(), nullable=True),
        sa.Column('manifest_bytes', sa.BigInteger(), nullable=True),
        sa.CheckConstraint("status in ('running', 'done', 'cancelled')", name='ck_sync_jobs_status'),
        sa.ForeignKeyConstraint(['club_id'], ['clubs.id']),
        sa.ForeignKeyConstraint(['created_by'], ['accounts.id']),
        sa.ForeignKeyConstraint(['source_pc_id'], ['pcs.id']),
        sa.PrimaryKeyConstraint('id'),
    )
    op.create_index(op.f('ix_sync_jobs_club_id'), 'sync_jobs', ['club_id'], unique=False)

    op.create_table(
        'sync_targets',
        sa.Column('id', sa.Integer(), nullable=False),
        sa.Column('job_id', sa.Integer(), nullable=False),
        sa.Column('pc_id', sa.Integer(), nullable=False),
        sa.Column('role', sa.String(length=6), nullable=False),
        sa.Column('status', sa.String(length=10), server_default='pending', nullable=False),
        sa.Column('bytes_done', sa.BigInteger(), server_default='0', nullable=False),
        sa.Column('bytes_total', sa.BigInteger(), server_default='0', nullable=False),
        sa.Column('bytes_fetched', sa.BigInteger(), server_default='0', nullable=False),
        sa.Column('bytes_from_peers', sa.BigInteger(), server_default='0', nullable=False),
        sa.Column('files_done', sa.Integer(), server_default='0', nullable=False),
        sa.Column('files_total', sa.Integer(), server_default='0', nullable=False),
        sa.Column('message', sa.String(length=200), nullable=True),
        sa.Column('serve_url', sa.String(length=80), nullable=True),
        sa.Column('updated_at', sa.DateTime(), nullable=True),
        sa.Column('finished_at', sa.DateTime(), nullable=True),
        sa.CheckConstraint("role in ('source', 'target')", name='ck_sync_targets_role'),
        sa.CheckConstraint("status in ('pending', 'scanning', 'waiting', 'syncing', 'ready', 'failed', 'cancelled')", name='ck_sync_targets_status'),
        sa.ForeignKeyConstraint(['job_id'], ['sync_jobs.id']),
        sa.ForeignKeyConstraint(['pc_id'], ['pcs.id']),
        sa.PrimaryKeyConstraint('id'),
        sa.UniqueConstraint('job_id', 'pc_id', name='uq_sync_targets_job_pc'),
    )
    op.create_index(op.f('ix_sync_targets_job_id'), 'sync_targets', ['job_id'], unique=False)
    op.create_index(op.f('ix_sync_targets_pc_id'), 'sync_targets', ['pc_id'], unique=False)


def downgrade() -> None:
    op.drop_index(op.f('ix_sync_targets_pc_id'), table_name='sync_targets')
    op.drop_index(op.f('ix_sync_targets_job_id'), table_name='sync_targets')
    op.drop_table('sync_targets')
    op.drop_index(op.f('ix_sync_jobs_club_id'), table_name='sync_jobs')
    op.drop_table('sync_jobs')
    with op.batch_alter_table('pcs', schema=None) as batch_op:
        batch_op.drop_column('games_wanted_at')
        batch_op.drop_column('games_at')
        batch_op.drop_column('games_json')
