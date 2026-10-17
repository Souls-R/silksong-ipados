"""Checks for destructive path mistakes and incomplete/import-incompatible packs."""
from pathlib import Path
import copy
import json
import plistlib
import tempfile
import unittest
import zipfile

from scripts.common import PACK_FOLDER, PACK_ID, REPO, output_directory, relative_bundle
from scripts.native import inspect_ipa
from scripts.prepare import ios_support
from scripts.resources import catalog_bundles, compatibility_id, resource_contract, validate_contract, validate_index


class BuildBoundaries(unittest.TestCase):
    def test_ios_support_accepts_mac_sibling_modules_and_embedded_layout(self):
        with tempfile.TemporaryDirectory() as tmp:
            install = Path(tmp)
            contents = install / 'Unity.app/Contents'
            contents.mkdir(parents=True)
            with self.assertRaises(ValueError):
                ios_support(contents)
            sibling = install / 'PlaybackEngines/iOSSupport'
            sibling.mkdir(parents=True)
            self.assertEqual(ios_support(contents), sibling)
            embedded = contents / 'PlaybackEngines/iOSSupport'
            embedded.mkdir(parents=True)
            self.assertEqual(ios_support(contents), sibling)
            self.assertEqual(ios_support(install), sibling)
            sibling.rmdir()
            self.assertEqual(ios_support(contents), embedded)

    def index(self):
        return dict(storage='documents', folder=PACK_FOLDER,
                    pack=dict(schema=1, pack_id=PACK_ID, compatibility=compatibility_id('a' * 32),
                              bundle_count=1, bundle_bytes=3),
                    bundles=[dict(bundle='folder/sample.bundle', bytes=3)])

    def test_bundle_paths_cannot_escape_or_use_windows_drives(self):
        for value in ('/a.bundle', '../a.bundle', 'a/../b.bundle', 'C:/a.bundle',
                      'a\\b.bundle', 'a//b.bundle', 'a/./b.bundle', 'a.bundle\0', 'a.dll'):
            with self.subTest(value=value), self.assertRaises(ValueError):
                relative_bundle(value)
        self.assertEqual(relative_bundle('folder/sample.bundle').as_posix(), 'folder/sample.bundle')

    def test_outputs_must_not_overlap_input_or_repository(self):
        with tempfile.TemporaryDirectory() as tmp:
            base = Path(tmp)
            game = base / 'game'
            for path in (game, game / 'output', base, REPO / 'output'):
                with self.subTest(path=path), self.assertRaises(ValueError):
                    output_directory(path, game)
            self.assertEqual(output_directory(base / 'output', game), (base / 'output').resolve())

    def test_manifest_rejects_wrong_version_duplicate_and_inconsistent_totals(self):
        index = self.index()
        self.assertIs(validate_index(index), index)
        mutations = []
        bad = copy.deepcopy(index); bad['pack']['pack_id'] = 'wrong'; mutations.append(bad)
        bad = copy.deepcopy(index); bad['pack']['bundle_bytes'] += 1; mutations.append(bad)
        bad = copy.deepcopy(index); bad['bundles'][0]['bytes'] = 0; mutations.append(bad)
        bad = copy.deepcopy(index); bad['bundles'].append(dict(bundle='FOLDER/SAMPLE.bundle', bytes=3)); mutations.append(bad)
        bad = copy.deepcopy(index); del bad['pack']['compatibility']; mutations.append(bad)
        bad = copy.deepcopy(index); bad['pack']['compatibility'] = 'old-version'; mutations.append(bad)
        bad = copy.deepcopy(index); bad['pack'] = None; mutations.append(bad)
        bad = copy.deepcopy(index); bad['bundles'][0] = None; mutations.append(bad)
        bad = copy.deepcopy(index); bad['bundles'][0]['bytes'] = True; mutations.append(bad)
        for bad in mutations:
            with self.assertRaises(ValueError):
                validate_index(bad)

    def test_contract_is_independent_of_encoded_bundle_sizes(self):
        first = self.index()
        second = copy.deepcopy(first)
        second['bundles'][0]['bytes'] = 7
        second['pack']['bundle_bytes'] = 7
        self.assertEqual(resource_contract(first), resource_contract(second))
        second['pack']['compatibility'] = compatibility_id('b' * 32)
        self.assertNotEqual(resource_contract(first), resource_contract(second))

    def test_contract_rejects_old_format_and_unsafe_or_duplicate_names(self):
        contract = resource_contract(self.index())
        with self.assertRaises(ValueError):
            validate_contract(self.index())
        for names in ([], ['../bad.bundle'], ['a.bundle', 'A.bundle'], [None]):
            with self.subTest(names=names), self.assertRaises(ValueError):
                validate_contract(dict(contract, bundles=names))

    def test_half_copied_files_cannot_be_used_for_program_preparation(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            with self.assertRaises(ValueError):
                validate_index(self.index(), root)
            (root / 'folder').mkdir()
            file = root / 'folder/sample.bundle'
            file.write_bytes(b'x')
            with self.assertRaises(ValueError):
                validate_index(self.index(), root)
            file.write_bytes(b'xyz')
            validate_index(self.index(), root)

    def test_dynamic_catalog_keys_are_included_not_only_scenes(self):
        class SyntheticCatalog:
            keys = {'Scenes/Test': None, 'DynamicAudio': None}
            def closure(self, key):
                name = 'scene' if key.startswith('Scenes/') else 'audio'
                return [dict(internal_id=f'{{runtime}}/StandaloneOSX/{name}.bundle')]
        self.assertEqual(catalog_bundles(SyntheticCatalog()), ['audio.bundle', 'scene.bundle'])

    def test_ipa_rejects_wrong_identity_and_embedded_large_resources(self):
        manifest = dict(bundle_id='org.example.test', version='0.1.0', build='1',
                        resource_contract=resource_contract(self.index()))
        with tempfile.TemporaryDirectory() as tmp:
            ipa = Path(tmp) / 'synthetic.zip'
            for bad_identity, bundles, bad_contract in ((False, False, False), (True, False, False),
                                                       (False, True, False), (False, False, True)):
                with zipfile.ZipFile(ipa, 'w') as z:
                    prefix = 'Payload/SilksongNative.app/'
                    info = dict(CFBundleIdentifier='wrong' if bad_identity else manifest['bundle_id'],
                                CFBundleShortVersionString='0.1.0', CFBundleVersion='1')
                    z.writestr(prefix + 'Info.plist', plistlib.dumps(info))
                    for name in ['Unity-iPhone', 'Frameworks/UnityFramework.framework/UnityFramework',
                                 'Data/Managed/Metadata/global-metadata.dat']:
                        z.writestr(prefix + name, b'synthetic')
                    contract = copy.deepcopy(manifest['resource_contract'])
                    if bad_contract:
                        contract['compatibility'] = compatibility_id('b' * 32)
                    z.writestr(prefix + 'Data/Raw/native-resource-index.json', json.dumps(contract))
                    if bundles:
                        z.writestr(prefix + 'Data/Raw/aa/StandaloneOSX/sample.bundle', b'synthetic')
                if bad_identity or bundles or bad_contract:
                    with self.assertRaises(ValueError):
                        inspect_ipa(ipa, manifest)
                else:
                    self.assertEqual(inspect_ipa(ipa, manifest)['embedded_large_bundles'], 0)


if __name__ == '__main__':
    unittest.main()
