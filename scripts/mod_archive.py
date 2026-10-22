"""Make the two-folder ZIP which Files expands into Documents/SilksongMods."""
import hashlib
import json
import shutil
import stat
import zipfile

from .common import output_directory, relative_bundle, write_json


def zip_mods(args):
    skin_path, hp_path = args.skin_pack.expanduser().resolve(), args.hpbar_pack.expanduser().resolve()
    output = output_directory(args.output, skin_path, hp_path)
    if output.name != 'SilksongMods.zip':
        raise ValueError('Use the filename SilksongMods.zip so Files creates the expected folder')
    if output.exists():
        raise ValueError('Use a fresh output ZIP')
    with zipfile.ZipFile(skin_path) as skin, zipfile.ZipFile(hp_path) as hp:
        manifest = json.loads(skin.read('SilksongSkin/skin.json'))
        info = json.loads(hp.read('Mods/mod-info.json'))
        if (manifest.get('schema') != 1 or not manifest.get('name') or not manifest.get('bundles')
                or info.get('schema') != 2 or not manifest.get('compatibility')
                or manifest['compatibility'] != info.get('compatibility')):
            raise ValueError('Skin and HP bar resource versions differ')
        required = {'SilksongSkin/skin.json'}
        seen = set()
        for row in manifest['bundles']:
            relative_bundle(row['bundle'])
            if row['bundle'].lower() in seen:
                raise ValueError('Duplicate skin bundle')
            seen.add(row['bundle'].lower())
            name = 'SilksongSkin/bundles/' + row['bundle']
            if row['bytes'] <= 0 or skin.getinfo(name).file_size != row['bytes']:
                raise ValueError('Incomplete skin: ' + name)
            required.add(name)
        expected_hp = {'Mods/mod-info.json', 'Mods/plugins/Silk_HpBarPro/healthbar_pro'}
        for archive, expected in ((skin, required), (hp, expected_hp)):
            if len(archive.namelist()) != len(expected) or set(archive.namelist()) != expected:
                raise ValueError('Unexpected files in mod input ZIP')
            if archive.testzip() is not None:
                raise ValueError('Corrupt mod input ZIP')
            for entry in archive.infolist():
                if stat.S_IFMT(entry.external_attr >> 16) not in (0, stat.S_IFREG):
                    raise ValueError('Only regular data files are allowed')
        output.parent.mkdir(parents=True, exist_ok=True)
        records = {}
        # Files supplies the enclosing SilksongMods folder. Do not add another.
        with zipfile.ZipFile(output, 'w', zipfile.ZIP_DEFLATED, compresslevel=1) as merged:
            for archive in (skin, hp):
                for entry in sorted(archive.infolist(), key=lambda row: row.filename):
                    target = zipfile.ZipInfo(entry.filename, entry.date_time)
                    target.compress_type = zipfile.ZIP_DEFLATED
                    target.create_system = 3
                    target.external_attr = (stat.S_IFREG | 0o644) << 16
                    target.file_size = entry.file_size
                    with archive.open(entry) as source, merged.open(target, 'w') as destination:
                        shutil.copyfileobj(source, destination, 1024 * 1024)
                    records[entry.filename] = (entry.file_size, entry.CRC)
    with zipfile.ZipFile(output) as check:
        assert {e.filename: (e.file_size, e.CRC) for e in check.infolist()} == records
        assert check.testzip() is None
    with output.open('rb') as stream:
        digest = hashlib.file_digest(stream, 'sha256').hexdigest()
    write_json(output.with_name('SilksongMods-package-report.json'), dict(schema=1,
        filename=output.name, extraction_folder='SilksongMods', minimum_build=34,
        compatibility=manifest['compatibility'], skin=manifest['name'], files=len(records),
        expanded_bytes=sum(row[0] for row in records.values()), bytes=output.stat().st_size,
        sha256=digest, contains_original_resources=False, contains_saves=False))
    print('Combined skin and HP bar ZIP: ' + str(output))
    return output
