from pathlib import Path
import tempfile
import unittest
import json
import zipfile

from scripts.mods import safe_path
from scripts.mod_skins import normalized, match_skin, package_skin
from scripts.mod_archive import zip_mods
from types import SimpleNamespace


class ModImportTests(unittest.TestCase):
    def test_files_combined_zip_has_two_roots_and_no_extra_enclosing_folder(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            skin = root / 'skin.zip'
            hpbar = root / 'hpbar.zip'
            with zipfile.ZipFile(skin, 'w') as archive:
                archive.writestr('SilksongSkin/skin.json', json.dumps(dict(schema=1, name='Skin A',
                    compatibility='v1', bundles=[dict(bundle='hero.bundle', bytes=4)])))
                archive.writestr('SilksongSkin/bundles/hero.bundle', b'hero')
            with zipfile.ZipFile(hpbar, 'w') as archive:
                archive.writestr('Mods/mod-info.json', json.dumps(dict(schema=2, compatibility='v1')))
                archive.writestr('Mods/plugins/Silk_HpBarPro/healthbar_pro', b'hp')
            output = zip_mods(SimpleNamespace(skin_pack=skin, hpbar_pack=hpbar, output=root/'SilksongMods.zip'))
            with zipfile.ZipFile(output) as archive:
                self.assertEqual({name.split('/')[0] for name in archive.namelist()}, {'SilksongSkin', 'Mods'})
                self.assertEqual(archive.read('SilksongSkin/bundles/hero.bundle'), b'hero')
                self.assertEqual(archive.read('Mods/plugins/Silk_HpBarPro/healthbar_pro'), b'hp')
            with self.assertRaises(ValueError):
                zip_mods(SimpleNamespace(skin_pack=skin, hpbar_pack=hpbar, output=root/'Renamed.zip'))
            with zipfile.ZipFile(hpbar, 'w') as archive:
                archive.writestr('Mods/mod-info.json', json.dumps(dict(schema=2, compatibility='v2')))
                archive.writestr('Mods/plugins/Silk_HpBarPro/healthbar_pro', b'hp')
            with self.assertRaises(ValueError):
                zip_mods(SimpleNamespace(skin_pack=skin, hpbar_pack=hpbar, output=root/'other/SilksongMods.zip'))

    def test_standalone_skin_contains_no_plugin_contract_and_excludes_leftovers(self):
        with tempfile.TemporaryDirectory() as tmp:
            output = Path(tmp)
            skin = output / 'SilksongSkin'
            (skin / 'bundles/sub').mkdir(parents=True)
            (skin / 'bundles/sub/hero.bundle').write_bytes(b'hero')
            (skin / 'bundles/old.bundle').write_bytes(b'old')
            archive = package_skin(skin, 'Skin A', 'v1', [dict(bundle='sub/hero.bundle', bytes=4)], output)
            with zipfile.ZipFile(archive) as pack:
                self.assertEqual(set(pack.namelist()), {'SilksongSkin/skin.json', 'SilksongSkin/bundles/sub/hero.bundle'})
                manifest = json.loads(pack.read('SilksongSkin/skin.json'))
                self.assertEqual(manifest['compatibility'], 'v1')
                self.assertEqual(manifest['bundles'], [dict(bundle='sub/hero.bundle', bytes=4)])
            for bad in ([dict(bundle='../escape.bundle', bytes=4)],
                        [dict(bundle='sub/hero.bundle', bytes=5)],
                        [dict(bundle='sub/hero.bundle', bytes=4)] * 2):
                with self.assertRaises(ValueError):
                    package_skin(skin, 'Skin B', 'v1', bad, output)

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
