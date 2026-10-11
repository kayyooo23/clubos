"""pc programs

Revision ID: 0012
Revises: 0011
Create Date: 2026-10-11

Список программ, найденных на ПК (ярлыки в меню «Пуск»): служба присылает его на сервер, а администратор выбирает из него программу для меню гостя,
вместо того чтобы набирать путь к exe руками. programs_wanted_at: администратор попросил обновить список (служба увидит это в ответе state).
"""
from typing import Sequence, Union

from alembic import op
import sqlalchemy as sa


revision: str = '0012'
down_revision: Union[str, Sequence[str], None] = '0011'
branch_labels: Union[str, Sequence[str], None] = None
depends_on: Union[str, Sequence[str], None] = None


def upgrade() -> None:
    op.add_column('pcs', sa.Column('programs_json', sa.Text(), nullable=True))
    op.add_column('pcs', sa.Column('programs_at', sa.DateTime(), nullable=True))
    op.add_column('pcs', sa.Column('programs_wanted_at', sa.DateTime(), nullable=True))


def downgrade() -> None:
    op.drop_column('pcs', 'programs_wanted_at')
    op.drop_column('pcs', 'programs_at')
    op.drop_column('pcs', 'programs_json')
