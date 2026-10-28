"""zones

Revision ID: 0015
Revises: 0014
Create Date: 2026-10-12

Залы как шаблоны для ПК: название, характеристики и свои тарифы. У ПК и у тарифа появляется zone_id.
Существующие залы из pcs.zone становятся шаблонами (по одному на название в клубе), а ПК привязываются к ним.
Тарифы остаются общими (zone_id пусто): у клуба, который ничего не настраивал, всё работает как раньше.
"""
from datetime import datetime, timezone
from typing import Sequence, Union

from alembic import op
import sqlalchemy as sa


revision: str = '0015'
down_revision: Union[str, Sequence[str], None] = '0014'
branch_labels: Union[str, Sequence[str], None] = None
depends_on: Union[str, Sequence[str], None] = None


def upgrade() -> None:
    op.create_table(
        'zones',
        sa.Column('id', sa.Integer(), nullable=False),
        sa.Column('club_id', sa.Integer(), nullable=False),
        sa.Column('name', sa.String(length=40), nullable=False),
        sa.Column('cpu', sa.String(length=100), nullable=True),
        sa.Column('gpu', sa.String(length=100), nullable=True),
        sa.Column('ram', sa.String(length=100), nullable=True),
        sa.Column('monitor', sa.String(length=100), nullable=True),
        sa.Column('peripherals', sa.String(length=200), nullable=True),
        sa.Column('note', sa.String(length=200), nullable=True),
        sa.Column('sort_order', sa.Integer(), server_default='100', nullable=False),
        sa.Column('created_at', sa.DateTime(), nullable=False),
        sa.ForeignKeyConstraint(['club_id'], ['clubs.id']),
        sa.PrimaryKeyConstraint('id'),
        sa.UniqueConstraint('club_id', 'name', name='uq_zones_club_name'),
    )
    op.create_index(op.f('ix_zones_club_id'), 'zones', ['club_id'], unique=False)

    for table in ('pcs', 'tariffs'):
        with op.batch_alter_table(table, schema=None) as batch_op:
            batch_op.add_column(sa.Column('zone_id', sa.Integer(), nullable=True))
            batch_op.create_foreign_key(f'fk_{table}_zone_id_zones', 'zones', ['zone_id'], ['id'])
            batch_op.create_index(f'ix_{table}_zone_id', ['zone_id'], unique=False)

    # залы, которые уже есть на картах клубов, становятся шаблонами; ПК привязываются по названию
    conn = op.get_bind()
    now = datetime.now(timezone.utc).replace(tzinfo=None)
    existing = conn.execute(sa.text("SELECT DISTINCT club_id, zone FROM pcs ORDER BY club_id, zone")).fetchall()
    for order, (club_id, zone) in enumerate(existing):
        insert = sa.text("INSERT INTO zones (club_id, name, sort_order, created_at) VALUES (:club, :name, :order, :now)").bindparams(
            sa.bindparam("now", type_=sa.DateTime()))                  # тип указан явно: SQLAlchemy сам превратит дату в строку для базы
        conn.execute(insert, {"club": club_id, "name": zone, "order": 100 + order, "now": now})
        zone_id = conn.execute(sa.text("SELECT id FROM zones WHERE club_id = :club AND name = :name"), {"club": club_id, "name": zone}).scalar()
        conn.execute(sa.text("UPDATE pcs SET zone_id = :zone_id WHERE club_id = :club AND zone = :name"),
                     {"zone_id": zone_id, "club": club_id, "name": zone})


def downgrade() -> None:
    for table in ('tariffs', 'pcs'):
        with op.batch_alter_table(table, schema=None) as batch_op:
            batch_op.drop_index(f'ix_{table}_zone_id')
            batch_op.drop_constraint(f'fk_{table}_zone_id_zones', type_='foreignkey')
            batch_op.drop_column('zone_id')
    op.drop_index(op.f('ix_zones_club_id'), table_name='zones')
    op.drop_table('zones')
