from pathlib import Path
import json
import re
import shutil
import subprocess
import tempfile
import unittest

from scripts.common import REPO

STRINGS = REPO / 'src/launch/strings.json'
HEADER = REPO / 'src/runtime/NativeVirtualPad.h'
PAD_KEYS = ['settingVirtualPad', 'settingVirtualPadHelp',
            'settingPadAuto', 'settingPadAlways', 'settingPadNever']
EDIT_KEYS = ['padEdit', 'padEditHint', 'padEditDone', 'padEditReset']
ENTRY_POINTS = 'Mode/SetMode/Editing/BeginEditing/EndEditing/Attach/Detach/RealDeviceInput/ReadState'.split('/')


class VirtualPadTests(unittest.TestCase):
    def test_display_modes_button_bits_and_stick_math(self):
        compiler = shutil.which('clang++') or shutil.which('g++')
        if not compiler:
            self.skipTest('Native pad tests require a C++ compiler')
        with tempfile.TemporaryDirectory() as tmp:
            program = Path(tmp) / 'pad-tests.exe'
            result = subprocess.run([compiler, '-std=c++17', str(REPO / 'tests/VirtualPadStateTests.cpp'),
                                     '-o', str(program)], capture_output=True, text=True)
            self.assertEqual(result.returncode, 0, result.stderr)
            result = subprocess.run([str(program)], capture_output=True, text=True)
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)

    def test_managed_bit_table_matches_the_native_one(self):
        native = re.findall(r'NativePad(\w+)\s*=\s*1\s*<<\s*(\d+)', HEADER.read_text(encoding='utf-8'))
        managed = re.findall(r'(\w+)\s*=\s*1\s*<<\s*(\d+)',
                             (REPO / 'src/runtime/NativeVirtualPad.cs').read_text(encoding='utf-8'))
        # The managed reader maps every native bit to a gamepad control, so both
        # tables must line up exactly or players lose controls silently.
        self.assertEqual(native, managed)
        self.assertEqual(len(native), 14)

    def test_launcher_row_and_runtime_hooks_are_wired(self):
        launch = (REPO / 'src/runtime/NativeLaunch.mm').read_text(encoding='utf-8')
        self.assertIn('#include "NativeVirtualPad.h"', launch)
        # The launcher reads the mode, persists it through the shared setter and
        # hands the screen over to the pad once gameplay starts.
        self.assertIn('NativeVirtualPad_Mode()', launch)
        self.assertIn('NativeVirtualPad_SetMode(', launch)
        self.assertIn('NativeVirtualPad_Attach();', launch)
        self.assertIn('NativeVirtualPad_Detach();', launch)
        # The row must cycle exactly the three modes.
        self.assertIn('mode >= NativeVirtualPadNever ? NativeVirtualPadAuto : mode + 1', launch)
        # The layout editor opens from its own row with localised captions, and
        # every launcher action stays inert while it owns the screen.
        self.assertIn('NativeVirtualPad_BeginEditing(', launch)
        self.assertIn('self.padEdit = [self button:Text(@"padEdit")', launch)
        # Enter, retry, toggle, pad, editPad, home and the key handler.
        self.assertEqual(launch.count('if (NativeVirtualPad_Editing()) return;'), 7)
        bootstrap = (REPO / 'src/runtime/NativeBootstrap.cs').read_text(encoding='utf-8')
        self.assertIn('host.AddComponent<NativeVirtualPad>()', bootstrap)
        pad = (REPO / 'src/runtime/NativeVirtualPad.mm').read_text(encoding='utf-8')
        # Every declared entry point needs a definition, or the link fails.
        for name in ENTRY_POINTS:
            self.assertIn(f'NativeVirtualPad_{name}', pad)
        # The editor must persist positions per control and keep them on-canvas.
        self.assertIn('PadLayoutKey', pad)
        for call in ('NativeVirtualPadClampToCanvas(', '- (void)saveLayout', '- (void)loadLayout',
                     'for (UIPanGestureRecognizer* drag in self.drags.allValues) drag.enabled = editing'):
            self.assertIn(call, pad)
        # A forced-off pad still has to be visible while it is being arranged,
        # and must not report a gamepad until the player is back in the game.
        self.assertIn('PadEditing || NativeVirtualPadVisible(NativeVirtualPad_Mode(), self.realDeviceInput)', pad)
        self.assertIn('if (PadEditing) { result->state = 0; return; }', pad)

    def test_generated_settings_and_build_list_include_the_pad(self):
        launch = (REPO / 'scripts/launch.py').read_text(encoding='utf-8')
        self.assertIn("Key='NativeVirtualPadMode'", launch)
        self.assertIn('PSMultiValueSpecifier', launch)
        configure = (REPO / 'scripts/configure_xcode.rb').read_text(encoding='utf-8')
        self.assertIn('NativeVirtualPad.mm', configure)

    def test_every_language_names_the_pad_setting(self):
        languages = json.loads(STRINGS.read_text(encoding='utf-8'))
        self.assertEqual(set(languages), {'zh-Hans', 'zh-Hant', 'en'})
        for name, values in languages.items():
            for key in PAD_KEYS + EDIT_KEYS:
                self.assertIn(key, values, f'{name} is missing {key}')
                self.assertTrue(values[key].strip(), f'{name}.{key} is empty')
            # The editor captions are launcher-only; they must not leak into the
            # app Settings bundle, which only picks up setting*/section* keys.
            for key in EDIT_KEYS:
                self.assertFalse(key.startswith(('setting', 'section')), f'{key} would leak into Settings')
