"""Checks for the Metal performance overlay switch on the launcher page."""
import json
import unittest

from scripts.common import REPO

LAUNCH = REPO / 'src/runtime/NativeLaunch.mm'
STRINGS = REPO / 'src/launch/strings.json'
STARTUP = REPO / 'scripts/launch.py'


class MetalHudTests(unittest.TestCase):
    def test_launcher_row_writes_the_preference_the_startup_reads(self):
        launch = LAUNCH.read_text(encoding='utf-8')
        # The override is read from this key before Unity loads Metal, so the row
        # has to toggle exactly the same preference as the Settings bundle.
        self.assertIn('@"NativeMetalHUD"', launch)
        self.assertIn('boolForKey:@"NativeMetalHUD"', STARTUP.read_text(encoding='utf-8'))
        self.assertIn('self.hudChoice = [self button:@"" y:650 action:@selector(hud:)]', launch)
        self.assertIn('self.hudValue.text = Text(enabled ? @"hudOn" : @"hudOff");', launch)
        self.assertIn('self.choices = @[enter, self.nextLaunch, self.padChoice, self.padEdit, self.hudChoice, home];', launch)
        # Keys, controller and touch must all reach the row, or the switch is
        # unreachable on some input device.
        self.assertEqual(launch.count('[self hud:nil]'), 2)
        self.assertIn('sendActionsForControlEvents:UIControlEventTouchUpInside', launch)

    def test_launcher_reports_that_a_change_needs_a_restart(self):
        launch = LAUNCH.read_text(encoding='utf-8')
        # The overlay reads its environment once, when the Metal device is
        # created; the shared hint line must say so instead of looking broken.
        self.assertIn('const char* value = getenv("MTL_HUD_ENABLED");', launch)
        self.assertIn('self.hudHint.text = Text(running == enabled ? @"noticeHelp" : @"hudRestart");', launch)

    def test_hud_captions_exist_in_every_language_and_stay_out_of_settings(self):
        languages = json.loads(STRINGS.read_text(encoding='utf-8'))
        self.assertEqual(set(languages), {'zh-Hans', 'zh-Hant', 'en'})
        for name, values in languages.items():
            for key in ('settingHud', 'hudOn', 'hudOff', 'hudRestart'):
                self.assertIn(key, values, f'{name} is missing {key}')
                self.assertTrue(values[key].strip(), f'{name}.{key} is empty')
            # Settings.bundle only exports setting*/section* captions.
            for key in ('hudOn', 'hudOff', 'hudRestart'):
                self.assertFalse(key.startswith(('setting', 'section')), f'{key} would leak into Settings')
