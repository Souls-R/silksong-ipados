from pathlib import Path
import tempfile
import unittest

from scripts.mods import safe_path
from scripts.mod_skins import normalized, match_skin


class ModImportTests(unittest.TestCase):
    def test_mod_paths_stay_in_the_selected_folder(self):
        root = Path(tempfile.gettempdir()).resolve()
        for bad in ('../data', '/etc/passwd', 'C:/data', 'x\\data', 'x//data', 'x/./data', 'x\0data'):
            with self.assertRaises(ValueError):
                safe_path(root, bad)
        self.assertEqual(safe_path(root, 'plugins/healthbar_pro'), root / 'plugins/healthbar_pro')

    def test_t2d_matching_ignores_only_the_atlas_hash(self):
        self.assertEqual(normalized('sactx-0-8192x4096-BC7-Core-22f3aabc'), 'sactx-0-8192x4096-BC7-Core')
        self.assertEqual(normalized('Font|Glow'), 'Font_Glow')
        self.assertEqual(normalized('ordinary-22f3aabc'), 'ordinary-22f3aabc')
        self.assertEqual(normalized('sactx-nonhex-suffix'), 'sactx-nonhex-suffix')

    def test_collection_name_is_not_replaced_by_texture_name_alone(self):
        from PIL import Image
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            (root / 'active.txt').touch()
            (root / 'Knight').mkdir()
            Image.new('RGBA', (4, 4)).save(root / 'Knight/atlas0.png')
            metadata = dict(textures={'hero': dict(name='atlas0', bundle='hero.bundle'),
                                      'enemy': dict(name='atlas0', bundle='enemy.bundle')},
                materials={'hero-mat': 'hero', 'enemy-mat': 'enemy'},
                collections=[dict(name='Knight', materials=['hero-mat']), dict(name='Enemy', materials=['enemy-mat'])])
            bundles, count = match_skin(root, metadata)
            self.assertEqual(count, 1)
            self.assertEqual(set(bundles), {'hero.bundle'})
