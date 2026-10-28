"""enroll codes and pc commands

Revision ID: 0016
Revises: 0015
Create Date: 2026-10-12

Подключение ПК по коду (enroll_codes + поля pcs: machine_id, enrolled_at, enroll_code_id, clone_alert_at)
и команды ПК от администратора (pc_commands).
"""
from typing import Sequence, Union

from alembic import op
import sqlalchemy as sa


revision: str = '0016'
down_revision: Union[str, Sequence[str], None] = '0015'
branch_labels: Union[str, Sequence[str], None] = None
depends_on: Union[str, Sequence[str], None] = None


def upgrade() -> None:
    op.create_table(
        'enroll_codes',
        sa.Column('id', sa.Integer(), nullable=False),
        sa.Column('club_id', sa.Integer(), nullable=False),
        sa.Column('code_hash', sa.String(length=64), nullable=False),
        sa.Column('code_hint', sa.String(length=8), nullable=False),
        sa.Column('label', sa.String(length=80), nullable=True),
        sa.Column('zone_id', sa.Integer(), nullable=True),
        sa.Column('created_by', sa.Integer(), nullable=True),
        sa.Column('created_at', sa.DateTime(), nullable=False),
        sa.Column('expires_at', sa.DateTime(), nullable=False),
        sa.Column('max_uses', sa.Integer(), nullable=True),
        sa.Column('used_count', sa.Integer(), server_default='0', nullable=False),
        sa.Column('revoked_at', sa.DateTime(), nullable=True),
        sa.ForeignKeyConstraint(['club_id'], ['clubs.id']),
        sa.ForeignKeyConstraint(['created_by'], ['accounts.id']),
        sa.ForeignKeyConstraint(['zone_id'], ['zones.id']),
        sa.PrimaryKeyConstraint('id'),
        sa.UniqueConstraint('code_hash'),
    )
    op.create_index(op.f('ix_enroll_codes_club_id'), 'enroll_codes', ['club_id'], unique=False)

    with op.batch_alter_table('pcs', schema=None) as batch_op:
        batch_op.add_column(sa.Column('machine_id', sa.String(length=64), nullable=True))
        batch_op.add_column(sa.Column('enrolled_at', sa.DateTime(), nullable=True))
        batch_op.add_column(sa.Column('enroll_code_id', sa.Integer(), nullable=True))
        batch_op.add_column(sa.Column('clone_alert_at', sa.DateTime(), nullable=True))
        batch_op.create_foreign_key('fk_pcs_enroll_code_id_enroll_codes', 'enroll_codes', ['enroll_code_id'], ['id'])

    op.create_table(
        'pc_commands',
        sa.Column('id', sa.Integer(), nullable=False),
        sa.Column('club_id', sa.Integer(), nullable=False),
        sa.Column('pc_id', sa.Integer(), nullable=False),
        sa.Column('kind', sa.String(length=30), nullable=False),
        sa.Column('payload', sa.String(length=500), nullable=True),
        sa.Column('status', sa.String(length=10), nullable=False),
        sa.Column('attempts', sa.Integer(), server_default='0', nullable=False),
        sa.Column('created_by', sa.Integer(), nullable=True),
        sa.Column('created_at', sa.DateTime(), nullable=False),
        sa.Column('sent_at', sa.DateTime(), nullable=True),
        sa.Column('finished_at', sa.DateTime(), nullable=True),
        sa.Column('result', sa.String(length=300), nullable=True),
        sa.Column('expires_at', sa.DateTime(), nullable=False),
        sa.CheckConstraint("status in ('pending', 'sent', 'done', 'failed', 'cancelled')", name='ck_pc_commands_status'),
        sa.ForeignKeyConstraint(['club_id'], ['clubs.id']),
        sa.ForeignKeyConstraint(['created_by'], ['accounts.id']),
        sa.ForeignKeyConstraint(['pc_id'], ['pcs.id']),
        sa.PrimaryKeyConstraint('id'),
    )
    op.create_index(op.f('ix_pc_commands_club_id'), 'pc_commands', ['club_id'], unique=False)
    op.create_index(op.f('ix_pc_commands_pc_id'), 'pc_commands', ['pc_id'], unique=False)


def downgrade() -> None:
    op.drop_index(op.f('ix_pc_commands_pc_id'), table_name='pc_commands')
    op.drop_index(op.f('ix_pc_commands_club_id'), table_name='pc_commands')
    op.drop_table('pc_commands')
    with op.batch_alter_table('pcs', schema=None) as batch_op:
        batch_op.drop_constraint('fk_pcs_enroll_code_id_enroll_codes', type_='foreignkey')
        batch_op.drop_column('clone_alert_at')
        batch_op.drop_column('enroll_code_id')
        batch_op.drop_column('enrolled_at')
        batch_op.drop_column('machine_id')
    op.drop_index(op.f('ix_enroll_codes_club_id'), table_name='enroll_codes')
    op.drop_table('enroll_codes')
