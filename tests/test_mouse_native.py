from pathlib import Path
import plistlib
import shutil
import subprocess
import tempfile
import unittest

from scripts.common import REPO
from scripts.mouse import configure_mouse


class NativeMouseTests(unittest.TestCase):
    def test_native_event_buffer_and_source_merging(self):
        compiler = shutil.which('clang++') or shutil.which('g++')
        if not compiler:
            self.skipTest('Native mouse tests require a C++ compiler')
        with tempfile.TemporaryDirectory() as tmp:
            program = Path(tmp) / 'mouse-tests.exe'
            result = subprocess.run([compiler, '-std=c++17', str(REPO / 'tests/MouseStateTests.cpp'),
                                     '-o', str(program)], capture_output=True, text=True)
            self.assertEqual(result.returncode, 0, result.stderr)
            result = subprocess.run([str(program)], capture_output=True, text=True)
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)

    def test_touch_boundary_is_complete_and_idempotent(self):
        with tempfile.TemporaryDirectory() as tmp:
            project = Path(tmp)
            source = project / 'Classes/UI/UnityView+iOS.mm'
            source.parent.mkdir(parents=True)
            source.write_text('#import "UnityView.h"\n' + '\n'.join(
                f'{{ UnitySendTouches{phase}(touches, event); }}'
                for phase in ('Begin', 'Ended', 'Cancelled', 'Moved')))
            info = project / 'Info.plist'
            info.write_bytes(plistlib.dumps({'CFBundleVersion': '1'}))
            configure_mouse(project)
            first = source.read_text()
            self.assertEqual(first.count('if (direct.count)'), 4)
            self.assertEqual(first.count('self, true'), 1)
            self.assertIn('UnitySendTouchesCancelled(direct, event)', first)
            configure_mouse(project)
            self.assertEqual(source.read_text(), first)
            self.assertTrue(plistlib.loads(info.read_bytes())['UIApplicationSupportsIndirectInputEvents'])
