"""guests wallet audit

Revision ID: 0002
Revises: 0001
Create Date: 2026-10-06

Добавляет: коды SMS, журнал денег, журнал действий, согласие гостя, таймаут кабинета клуба,
«один активный вход на ПК», «одна активная сессия на гостя», sessions.account_id NOT NULL.

Важно про SQLite: batch alter_column пересоздаёт таблицу и может потерять условие WHERE у частичных индексов.
Поэтому индексы sessions сначала снимаются, а потом создаются заново, уже после изменения колонки.
"""
from typing import Sequence, Union

from alembic import op
import sqlalchemy as sa


revision: str = '0002'
down_revision: Union[str, Sequence[str], None] = '0001'
branch_labels: Union[str, Sequence[str], None] = None
depends_on: Union[str, Sequence[str], None] = None

_ACTIVE = sa.text('ended_at IS NULL')


def upgrade() -> None:
    # 1. sessions меняем ПЕРВОЙ, пока на неё ещё не ссылаются новые таблицы
    op.drop_index('uq_sessions_one_active_per_pc', table_name='sessions')
    with op.batch_alter_table('sessions', schema=None) as batch_op:
        batch_op.alter_column('account_id', existing_type=sa.INTEGER(), nullable=False)
    op.create_index('uq_sessions_one_active_per_pc', 'sessions', ['pc_id'], unique=True,
                    sqlite_where=_ACTIVE, postgresql_where=_ACTIVE)
    op.create_index('uq_sessions_one_active_per_account', 'sessions', ['account_id'], unique=True,
                    sqlite_where=_ACTIVE, postgresql_where=_ACTIVE)

    # 2. новые колонки и индексы
    with op.batch_alter_table('accounts', schema=None) as batch_op:
        batch_op.add_column(sa.Column('consent_at', sa.DateTime(), nullable=True))
    with op.batch_alter_table('clubs', schema=None) as batch_op:
        batch_op.add_column(sa.Column('idle_logout_minutes', sa.Integer(), server_default='5', nullable=False))
    op.create_index('uq_logins_one_active_per_pc', 'logins', ['pc_id'], unique=True,
                    sqlite_where=_ACTIVE, postgresql_where=_ACTIVE)

    # 3. новые таблицы
    op.create_table('audit_log',
        sa.Column('id', sa.Integer(), nullable=False),
        sa.Column('club_id', sa.Integer(), nullable=False),
        sa.Column('actor_account_id', sa.Integer(), nullable=True),
        sa.Column('pc_id', sa.Integer(), nullable=True),
        sa.Column('action', sa.String(length=40), nullable=False),
        sa.Column('details', sa.String(length=500), nullable=True),
        sa.Column('ip', sa.String(length=45), nullable=True),
        sa.Column('created_at', sa.DateTime(), nullable=False),
        sa.ForeignKeyConstraint(['actor_account_id'], ['accounts.id'], ),
        sa.ForeignKeyConstraint(['club_id'], ['clubs.id'], ),
        sa.ForeignKeyConstraint(['pc_id'], ['pcs.id'], ),
        sa.PrimaryKeyConstraint('id')
    )
    op.create_index(op.f('ix_audit_log_club_id'), 'audit_log', ['club_id'], unique=False)

    op.create_table('phone_verifications',
        sa.Column('id', sa.Integer(), nullable=False),
        sa.Column('club_id', sa.Integer(), nullable=False),
        sa.Column('phone', sa.String(length=20), nullable=False),
        sa.Column('purpose', sa.String(length=10), nullable=False),
        sa.Column('code_hash', sa.String(length=64), nullable=False),
        sa.Column('expires_at', sa.DateTime(), nullable=False),
        sa.Column('attempts', sa.Integer(), nullable=False),
        sa.Column('consumed_at', sa.DateTime(), nullable=True),
        sa.Column('created_at', sa.DateTime(), nullable=False),
        sa.Column('ip', sa.String(length=45), nullable=True),
        sa.Column('pc_id', sa.Integer(), nullable=True),
        sa.CheckConstraint("purpose in ('register', 'reset')", name='ck_phone_verifications_purpose'),
        sa.ForeignKeyConstraint(['club_id'], ['clubs.id'], ),
        sa.ForeignKeyConstraint(['pc_id'], ['pcs.id'], ),
        sa.PrimaryKeyConstraint('id')
    )
    op.create_index(op.f('ix_phone_verifications_club_id'), 'phone_verifications', ['club_id'], unique=False)
    op.create_index('ix_phone_verifications_lookup', 'phone_verifications', ['club_id', 'phone', 'purpose'], unique=False)

    op.create_table('wallet_transactions',
        sa.Column('id', sa.Integer(), nullable=False),
        sa.Column('club_id', sa.Integer(), nullable=False),
        sa.Column('account_id', sa.Integer(), nullable=False),
        sa.Column('amount', sa.Integer(), nullable=False),
        sa.Column('balance_after', sa.Integer(), nullable=False),
        sa.Column('kind', sa.String(length=20), nullable=False),
        sa.Column('session_id', sa.Integer(), nullable=True),
        sa.Column('staff_id', sa.Integer(), nullable=True),
        sa.Column('comment', sa.String(length=200), nullable=True),
        sa.Column('created_at', sa.DateTime(), nullable=False),
        sa.CheckConstraint("kind in ('topup_cash', 'topup_online', 'session_charge', 'adjustment')", name='ck_wallet_transactions_kind'),
        sa.CheckConstraint('amount != 0', name='ck_wallet_transactions_amount'),
        sa.ForeignKeyConstraint(['account_id'], ['accounts.id'], ),
        sa.ForeignKeyConstraint(['club_id'], ['clubs.id'], ),
        sa.ForeignKeyConstraint(['session_id'], ['sessions.id'], ),
        sa.ForeignKeyConstraint(['staff_id'], ['accounts.id'], ),
        sa.PrimaryKeyConstraint('id')
    )
    op.create_index(op.f('ix_wallet_transactions_account_id'), 'wallet_transactions', ['account_id'], unique=False)
    op.create_index(op.f('ix_wallet_transactions_club_id'), 'wallet_transactions', ['club_id'], unique=False)


def downgrade() -> None:
    op.drop_index(op.f('ix_wallet_transactions_club_id'), table_name='wallet_transactions')
    op.drop_index(op.f('ix_wallet_transactions_account_id'), table_name='wallet_transactions')
    op.drop_table('wallet_transactions')
    op.drop_index('ix_phone_verifications_lookup', table_name='phone_verifications')
    op.drop_index(op.f('ix_phone_verifications_club_id'), table_name='phone_verifications')
    op.drop_table('phone_verifications')
    op.drop_index(op.f('ix_audit_log_club_id'), table_name='audit_log')
    op.drop_table('audit_log')

    op.drop_index('uq_logins_one_active_per_pc', table_name='logins')
    with op.batch_alter_table('clubs', schema=None) as batch_op:
        batch_op.drop_column('idle_logout_minutes')
    with op.batch_alter_table('accounts', schema=None) as batch_op:
        batch_op.drop_column('consent_at')

    op.drop_index('uq_sessions_one_active_per_account', table_name='sessions')
    op.drop_index('uq_sessions_one_active_per_pc', table_name='sessions')
    with op.batch_alter_table('sessions', schema=None) as batch_op:
        batch_op.alter_column('account_id', existing_type=sa.INTEGER(), nullable=True)
    op.create_index('uq_sessions_one_active_per_pc', 'sessions', ['pc_id'], unique=True,
                    sqlite_where=_ACTIVE, postgresql_where=_ACTIVE)
