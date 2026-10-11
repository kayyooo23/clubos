"""Логика админки, которую удобно проверять без браузера (нужен Node; без него тест пропускается)."""
import shutil
import subprocess
from pathlib import Path

import pytest

SCRIPT = Path(__file__).parent / "js" / "admin_logic.mjs"


@pytest.mark.skipif(shutil.which("node") is None, reason="Node.js не установлен")
def test_admin_logic_in_node():
    result = subprocess.run(["node", str(SCRIPT)], capture_output=True, text=True, timeout=60)
    assert result.returncode == 0, result.stdout + result.stderr
    assert result.stdout.strip().endswith("ok")
