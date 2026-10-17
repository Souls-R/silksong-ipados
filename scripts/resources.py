"""Derive the complete Documents pack and program catalog from user files."""
from concurrent.futures import ProcessPoolExecutor
from pathlib import Path
import hashlib
import json
import re
import shutil
import struct
import zipfile

from .catalog import Catalog
from .common import (PACK_FOLDER, PACK_ID, RESOURCE_REVISION, UNITY_VERSION, output_directory, read_json,
                     relative_bundle, require_files, write_json)


def catalog_bundles(catalog):
    names = set()
    for key in catalog.keys:
        for loc in catalog.closure(key):
            value = loc['internal_id']
            if '/StandaloneOSX/' in value and value.endswith('.bundle'):
                name = value.split('/StandaloneOSX/', 1)[1]
                relative_bundle(name)
                names.add(name)
    if not names:
        raise ValueError('No macOS bundles found in the supplied catalog')
    return sorted(names)


def adapt_catalog(source, destination):
    """Append the required atlas alias; retain original keys and locations."""
    c = Catalog(source)
    b = bytearray(c.data)

    def raw(data):
        offset = len(b)
        b.extend(data)
        return offset

    def values(*items):
        return raw(struct.pack('<' + 'I' * len(items), *items))

    def text(value):
        data = value.encode('ascii')
        values(len(data))
        return raw(data)

    def array(items):
        values(len(items) * 4)
        return values(*items)

    alias = 'Assets/Sprites/_Atlases/Load_Icon_Knight.spriteatlas'
    if alias in c.keys:
        raise ValueError('Unexpected catalog: startup alias already exists')
    keys = [k for k in c.keys if k.startswith(
        'atlases_assets_assets/sprites/_atlases/load_icon_knight.spriteatlas_')
        and k.endswith('.bundle')]
    if len(keys) != 1:
        raise ValueError('Unsupported catalog: expected one startup atlas bundle')
    bundle_location, = c.keys[keys[0]]
    assert c.location(bundle_location)['type'].endswith('IAssetBundleResource')
    key_name, _ = c.values(c.header[2], 2)
    string_type, _ = c.values(key_name, 2)
    alias_string = text(alias)
    alias_object = values(string_type, values(alias_string, 0))
    atlas_type = values(text('UnityEngine.CoreModule'), text('UnityEngine.U2D.SpriteAtlas'))
    provider = text('UnityEngine.ResourceManagement.ResourceProviders.BundledAssetProvider')
    location = values(alias_string, alias_string, provider, array([bundle_location]),
                      0, 0xffffffff, atlas_type)
    location_set = array([location])
    old_keys = c.data[c.header[2]:c.header[2] + c.values(c.header[2] - 4)[0]]
    values(len(old_keys) + 8)
    new_keys = raw(old_keys + struct.pack('<II', alias_object, location_set))
    struct.pack_into('<I', b, 8, new_keys)
    destination.mkdir(parents=True, exist_ok=True)
    (destination / 'catalog.bin').write_bytes(b)
    # Addressables' small catalog identifier, not a resource-integrity scan.
    (destination / 'catalog.hash').write_text(hashlib.md5(b).hexdigest(), encoding='ascii')
    adapted = Catalog(bytes(b))
    assert set(adapted.keys) == set(c.keys) | {alias}
    assert all(adapted.keys[k] == v for k, v in c.keys.items())
    assert len(adapted.closure(alias)) == 2


def convert_job(job):
    from .texture_conversion import convert_bundle
    source, target, record, refresh = job
    return convert_bundle(Path(source), Path(target), Path(record), refresh)


def compatibility_id(catalog_id):
    # Reuse Addressables' existing small catalog identifier; never hash bundles.
    if not re.fullmatch(r'[0-9a-f]{32}', catalog_id):
        raise ValueError('Invalid resource catalog identifier')
    return f'{UNITY_VERSION}:ios-r{RESOURCE_REVISION}:{catalog_id}'


def validate_contract(contract):
    if (not isinstance(contract, dict) or contract.get('schema') != 2 or contract.get('storage') != 'documents'
            or contract.get('folder') != PACK_FOLDER or contract.get('pack_id') != PACK_ID
            or not isinstance(contract.get('compatibility'), str) or not contract['compatibility']):
        raise ValueError('Invalid application resource contract')
    names = contract.get('bundles')
    if not isinstance(names, list) or not names:
        raise ValueError('Empty application resource contract')
    for name in names:
        relative_bundle(name)
    if len({n.lower() for n in names}) != len(names):
        raise ValueError('Duplicate application resource entry')
    return contract


def resource_contract(index):
    validate_index(index)
    return validate_contract(dict(schema=2, storage='documents', folder=PACK_FOLDER,
                                  pack_id=PACK_ID, compatibility=index['pack']['compatibility'],
                                  bundles=sorted(r['bundle'] for r in index['bundles'])))


def validate_index(index, bundle_root=None):
    if not isinstance(index, dict) or index.get('storage') != 'documents' or index.get('folder') != PACK_FOLDER:
        raise ValueError('Unsupported resource storage/folder')
    records = index.get('bundles')
    if not isinstance(records, list) or not records:
        raise ValueError('Empty resource index')
    pack = index.get('pack')
    if not isinstance(pack, dict):
        raise ValueError('Invalid resource manifest')
    compatibility = pack.get('compatibility', '')
    prefix = f'{UNITY_VERSION}:ios-r{RESOURCE_REVISION}:'
    if not isinstance(compatibility, str) or not re.fullmatch(re.escape(prefix) + r'[0-9a-f]{32}', compatibility):
        raise ValueError('Missing or unsupported resource compatibility version; rerun resources')
    names = set()
    for record in records:
        if not isinstance(record, dict):
            raise ValueError('Invalid resource entry')
        name = record.get('bundle')
        relative_bundle(name)
        if name.lower() in names or type(record.get('bytes')) is not int or not 0 < record['bytes'] <= 2**63-1:
            raise ValueError('Duplicate or empty resource entry')
        names.add(name.lower())
        if bundle_root is not None:
            file = bundle_root / name
            if not file.is_file() or file.stat().st_size != record['bytes']:
                raise ValueError(f'Missing/incomplete resource: {name}')
    expected = dict(schema=1, pack_id=PACK_ID, compatibility=compatibility, bundle_count=len(records),
                    bundle_bytes=sum(r['bytes'] for r in records))
    if expected['bundle_bytes'] > 2**63-1 or index.get('pack') != expected:
        raise ValueError('Invalid resource manifest totals/version')
    return index


def prepare_resources(args):
    game = args.game_data.expanduser().resolve()
    require_files(game, ['Managed/Assembly-CSharp.dll', 'StreamingAssets/aa/catalog.bin',
                         'StreamingAssets/aa/settings.json'])
    output = output_directory(args.output, game)
    if output.exists() and not (output / '.resource-work.json').is_file():
        raise ValueError('Existing output is not a builder resource directory')
    source = game / 'StreamingAssets/aa'
    catalog = Catalog(source / 'catalog.bin')
    names = catalog_bundles(catalog)
    bundle_source = source / 'StandaloneOSX'
    require_files(bundle_source, names)
    state = dict(schema=1, source=str(game))
    marker = output / '.resource-work.json'
    if marker.exists() and read_json(marker) != state:
        raise ValueError('Resource output belongs to another input; use a fresh directory')
    write_json(marker, state)
    # Do not advertise a complete pack while an interrupted rebuild is underway.
    pack = output / PACK_FOLDER
    complete = pack / 'pack.json'
    if complete.exists():
        complete.unlink()
    bundle_output = pack / 'aa/StandaloneOSX'
    jobs = [(str(bundle_source / n), str(bundle_output / n),
             str(output / 'cache' / (n + '.json')), args.refresh) for n in names]
    records = []
    with ProcessPoolExecutor(max_workers=args.jobs) as pool:
        for name, result in zip(names, pool.map(convert_job, jobs, chunksize=1)):
            records.append(dict(bundle=name, bytes=result['bytes']))
            print(f'{len(records)}/{len(names)} {"reuse" if result["reused"] else "converted"}: {name}', flush=True)
    program = output / 'program/Raw'
    adapt_catalog(source / 'catalog.bin', program / 'aa')
    compatibility = compatibility_id((program / 'aa/catalog.hash').read_text().strip())
    index = dict(storage='documents', folder=PACK_FOLDER, all_catalog_scenes=True,
                 pack=dict(schema=1, pack_id=PACK_ID, compatibility=compatibility, bundle_count=len(records),
                           bundle_bytes=sum(r['bytes'] for r in records)), bundles=records)
    validate_index(index, bundle_output)
    settings = read_json(source / 'settings.json')
    settings['m_buildTarget'] = 'iOS'
    settings['m_LogResourceManagerExceptions'] = True
    write_json(program / 'aa/settings.json', settings)
    for name in ('BuildMetadata.json', 'StreamingAssets.txt'):
        src = game / 'StreamingAssets' / name
        if src.is_file():
            shutil.copyfile(src, program / name)
    write_json(program / 'native-resource-index.json', resource_contract(index))
    write_json(pack / 'resource-index.json', index)
    write_json(complete, index['pack'])
    print(json.dumps(index['pack'], indent=2), flush=True)


def zip_resources(args):
    root = args.resources.expanduser().resolve()
    pack = root / PACK_FOLDER
    index = validate_index(read_json(pack / 'resource-index.json'), pack / 'aa/StandaloneOSX')
    if read_json(pack / 'pack.json') != index['pack']:
        raise ValueError('Resource pack is not complete')
    output = args.output.expanduser().resolve()
    if output.exists() or output.with_suffix(output.suffix + '.partial').exists():
        raise ValueError('Refusing to overwrite an existing resource ZIP')
    output.parent.mkdir(parents=True, exist_ok=True)
    temporary = output.with_suffix(output.suffix + '.partial')
    with zipfile.ZipFile(temporary, 'x', compression=zipfile.ZIP_DEFLATED, compresslevel=1) as archive:
        for record in index['bundles']:
            relative = 'aa/StandaloneOSX/' + record['bundle']
            archive.write(pack / relative, PACK_FOLDER + '/' + relative)
        for name in ('resource-index.json', 'pack.json'):
            archive.write(pack / name, PACK_FOLDER + '/' + name)
    temporary.rename(output)
    print(f'Resource ZIP: {output}')
