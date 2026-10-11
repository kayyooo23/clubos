"""app cover source: program

Revision ID: 0013
Revises: 0012
Create Date: 2026-10-11

Картинка записи может быть иконкой программы, которую прислал ПК (source = 'program'): сервер ставит её сам, когда путь к exe совпал
с найденной на ПК программой, и заменяет, когда путь поменяли. Ручная (manual) и из Steam остаются как были.
"""
from typing import Sequence, Union

from alembic import op


revision: str = '0013'
down_revision: Union[str, Sequence[str], None] = '0012'
branch_labels: Union[str, Sequence[str], None] = None
depends_on: Union[str, Sequence[str], None] = None


def upgrade() -> None:
    with op.batch_alter_table('app_covers') as batch:
        batch.drop_constraint('ck_app_covers_source', type_='check')
        batch.create_check_constraint('ck_app_covers_source', "source in ('steam', 'manual', 'program')")


def downgrade() -> None:
    op.execute("DELETE FROM app_covers WHERE source = 'program'")
    with op.batch_alter_table('app_covers') as batch:
        batch.drop_constraint('ck_app_covers_source', type_='check')
        batch.create_check_constraint('ck_app_covers_source', "source in ('steam', 'manual')")
