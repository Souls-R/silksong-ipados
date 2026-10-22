from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest

from scripts.common import REPO


class ControllerInputTests(unittest.TestCase):
    def test_native_touchpad_edges_and_lifecycle(self):
        compiler = shutil.which('clang++') or shutil.which('g++')
        if not compiler:
            self.skipTest('Native controller tests require a C++ compiler')
        with tempfile.TemporaryDirectory() as tmp:
            program = Path(tmp) / 'controller-state.exe'
            subprocess.run([compiler, '-std=c++17', str(REPO / 'tests/ControllerStateTests.cpp'),
                            '-o', str(program)], check=True, capture_output=True, text=True)
            result = subprocess.run([str(program)], capture_output=True, text=True)
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)


if __name__ == '__main__':
    unittest.main()
