"""shop orders calls

Revision ID: 0005
Revises: 0004
Create Date: 2026-10-06

Бар клуба и вызов администратора: товары, заказы гостей (оплата с баланса), строки заказов, вызовы.
В журнал денег добавляются виды операций shop_purchase и shop_refund.
"""
from typing import Sequence, Union

from alembic import op
import sqlalchemy as sa


revision: str = '0005'
down_revision: Union[str, Sequence[str], None] = '0004'
branch_labels: Union[str, Sequence[str], None] = None
depends_on: Union[str, Sequence[str], None] = None

_OLD_KINDS = "kind in ('topup_cash', 'topup_online', 'session_charge', 'adjustment')"
_NEW_KINDS = "kind in ('topup_cash', 'topup_online', 'session_charge', 'adjustment', 'shop_purchase', 'shop_refund')"


def _swap_kinds(old: str, new: str) -> None:
    with op.batch_alter_table('wallet_transactions', schema=None) as batch_op:
        batch_op.drop_constraint('ck_wallet_transactions_kind', type_='check')
        batch_op.create_check_constraint('ck_wallet_transactions_kind', new)


def upgrade() -> None:
    _swap_kinds(_OLD_KINDS, _NEW_KINDS)

    op.create_table(
        'products',
        sa.Column('id', sa.Integer(), nullable=False),
        sa.Column('club_id', sa.Integer(), nullable=False),
        sa.Column('name', sa.String(length=80), nullable=False),
        sa.Column('category', sa.String(length=40), nullable=False),
        sa.Column('price', sa.Integer(), nullable=False),
        sa.Column('sort_order', sa.Integer(), server_default='100', nullable=False),
        sa.Column('active', sa.Boolean(), server_default='1', nullable=False),
        sa.Column('created_at', sa.DateTime(), nullable=False),
        sa.CheckConstraint('price > 0', name='ck_products_price'),
        sa.ForeignKeyConstraint(['club_id'], ['clubs.id']),
        sa.PrimaryKeyConstraint('id'),
    )
    op.create_index(op.f('ix_products_club_id'), 'products', ['club_id'], unique=False)

    op.create_table(
        'orders',
        sa.Column('id', sa.Integer(), nullable=False),
        sa.Column('club_id', sa.Integer(), nullable=False),
        sa.Column('pc_id', sa.Integer(), nullable=False),
        sa.Column('account_id', sa.Integer(), nullable=False),
        sa.Column('total', sa.Integer(), nullable=False),
        sa.Column('status', sa.String(length=10), nullable=False),
        sa.Column('comment', sa.String(length=200), nullable=True),
        sa.Column('created_at', sa.DateTime(), nullable=False),
        sa.Column('updated_at', sa.DateTime(), nullable=False),
        sa.Column('handled_by', sa.Integer(), nullable=True),
        sa.CheckConstraint("status in ('new', 'accepted', 'done', 'cancelled')", name='ck_orders_status'),
        sa.CheckConstraint('total > 0', name='ck_orders_total'),
        sa.ForeignKeyConstraint(['account_id'], ['accounts.id']),
        sa.ForeignKeyConstraint(['club_id'], ['clubs.id']),
        sa.ForeignKeyConstraint(['handled_by'], ['accounts.id']),
        sa.ForeignKeyConstraint(['pc_id'], ['pcs.id']),
        sa.PrimaryKeyConstraint('id'),
    )
    op.create_index(op.f('ix_orders_account_id'), 'orders', ['account_id'], unique=False)
    op.create_index(op.f('ix_orders_club_id'), 'orders', ['club_id'], unique=False)

    op.create_table(
        'order_items',
        sa.Column('id', sa.Integer(), nullable=False),
        sa.Column('order_id', sa.Integer(), nullable=False),
        sa.Column('product_id', sa.Integer(), nullable=True),
        sa.Column('name', sa.String(length=80), nullable=False),
        sa.Column('price', sa.Integer(), nullable=False),
        sa.Column('qty', sa.Integer(), nullable=False),
        sa.CheckConstraint('qty > 0', name='ck_order_items_qty'),
        sa.ForeignKeyConstraint(['order_id'], ['orders.id']),
        sa.ForeignKeyConstraint(['product_id'], ['products.id']),
        sa.PrimaryKeyConstraint('id'),
    )
    op.create_index(op.f('ix_order_items_order_id'), 'order_items', ['order_id'], unique=False)

    op.create_table(
        'calls',
        sa.Column('id', sa.Integer(), nullable=False),
        sa.Column('club_id', sa.Integer(), nullable=False),
        sa.Column('pc_id', sa.Integer(), nullable=False),
        sa.Column('account_id', sa.Integer(), nullable=True),
        sa.Column('text', sa.String(length=100), nullable=True),
        sa.Column('status', sa.String(length=6), nullable=False),
        sa.Column('created_at', sa.DateTime(), nullable=False),
        sa.Column('handled_at', sa.DateTime(), nullable=True),
        sa.Column('handled_by', sa.Integer(), nullable=True),
        sa.CheckConstraint("status in ('open', 'done')", name='ck_calls_status'),
        sa.ForeignKeyConstraint(['account_id'], ['accounts.id']),
        sa.ForeignKeyConstraint(['club_id'], ['clubs.id']),
        sa.ForeignKeyConstraint(['handled_by'], ['accounts.id']),
        sa.ForeignKeyConstraint(['pc_id'], ['pcs.id']),
        sa.PrimaryKeyConstraint('id'),
    )
    op.create_index(op.f('ix_calls_club_id'), 'calls', ['club_id'], unique=False)
    # на одном ПК не больше одного открытого вызова, даже если два нажатия пришли одновременно
    op.create_index('uq_calls_one_open_per_pc', 'calls', ['pc_id'], unique=True,
                    sqlite_where=sa.text("status = 'open'"), postgresql_where=sa.text("status = 'open'"))


def downgrade() -> None:
    op.drop_index('uq_calls_one_open_per_pc', table_name='calls')
    op.drop_index(op.f('ix_calls_club_id'), table_name='calls')
    op.drop_table('calls')
    op.drop_index(op.f('ix_order_items_order_id'), table_name='order_items')
    op.drop_table('order_items')
    op.drop_index(op.f('ix_orders_club_id'), table_name='orders')
    op.drop_index(op.f('ix_orders_account_id'), table_name='orders')
    op.drop_table('orders')
    op.drop_index(op.f('ix_products_club_id'), table_name='products')
    op.drop_table('products')
    # записи магазина в журнале денег остаются без «своего» вида: при откате переводим их в корректировки
    op.execute("UPDATE wallet_transactions SET kind = 'adjustment' WHERE kind IN ('shop_purchase', 'shop_refund')")
    _swap_kinds(_NEW_KINDS, _OLD_KINDS)
