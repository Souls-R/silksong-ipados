"""Import Customizer atlas folders and CustomizerT2D Texture2D PNGs offline.

Only texture payloads change. Sprite definitions, UVs, materials, scripts and
animations are retained from the matching converted resource pack.
"""
from collections import defaultdict
from pathlib import Path
import gc
import hashlib
import re
import zipfile
from concurrent.futures import ProcessPoolExecutor

from .common import UNITY_VERSION, write_json, relative_bundle, output_directory


def package_skin(root, name, compatibility, bundles, output):
    """Package already verified iOS bundles without any compiled plugin inputs."""
    if root.name != 'SilksongSkin' or not name or not bundles:
        raise ValueError('Invalid standalone skin')
    records, names = [], set()
    for row in bundles:
        relative_bundle(row['bundle'])
        if row['bundle'].lower() in names:
            raise ValueError('Duplicate skin bundle')
        names.add(row['bundle'].lower())
        path = root / 'bundles' / row['bundle']
        if row['bytes'] <= 0 or path.stat().st_size != row['bytes']:
            raise ValueError('Incomplete skin bundle: ' + row['bundle'])
        records.append(dict(bundle=row['bundle'], bytes=row['bytes']))
    manifest = dict(schema=1, name=name, compatibility=compatibility,
                    bundles=sorted(records, key=lambda r: r['bundle']))
    write_json(root / 'skin.json', manifest)
    archive_path = output / 'SilksongSkin-ios.zip'
    # Only listed files are included; leftovers cannot silently join a skin.
    with zipfile.ZipFile(archive_path, 'w', zipfile.ZIP_DEFLATED, compresslevel=1) as archive:
        archive.write(root / 'skin.json', 'SilksongSkin/skin.json')
        for row in manifest['bundles']:
            relative = 'bundles/' + row['bundle']
            archive.write(root / relative, 'SilksongSkin/' + relative)
    with archive_path.open('rb') as stream:
        digest = hashlib.file_digest(stream, 'sha256').hexdigest()
    write_json(output / 'skin-pack-report.json', dict(schema=1, name=name,
        compatibility=compatibility, bundles=len(records), bytes=archive_path.stat().st_size,
        sha256=digest, device_verified=False))
    print('Standalone skin: ' + str(archive_path), flush=True)
    return archive_path


def prepare_skin_pack(args):
    from .mods import converted_index
    game, skin, resources = [p.expanduser().resolve() for p in
                             (args.game_data, args.skin, args.resources)]
    output = output_directory(args.output, game, skin, resources)
    if output.exists():
        raise ValueError('Use a fresh skin output directory')
    index = converted_index(resources)
    output.mkdir(parents=True)
    metadata = scan_metadata(game, output / 'skin-metadata.json')
    selections, pngs = match_skin(skin, metadata)
    if set(selections) - {r['bundle'] for r in index['bundles']}:
        raise ValueError('Skin does not match the converted resource catalog')
    root = output / 'data/SilksongSkin'
    jobs = [(str(resources), str(root / 'bundles'), name, rows) for name, rows in sorted(selections.items())]
    results = []
    with ProcessPoolExecutor(max_workers=args.jobs) as pool:
        for row in pool.map(overlay_bundle, jobs):
            results.append(row)
            print(f'Skin: {len(results)}/{len(jobs)} bundles ({row["bundle"]})', flush=True)
    write_json(output / 'skin-conversion.json', dict(pngs=pngs, bundles=results))
    return package_skin(root, skin.name, index['pack']['compatibility'], results, output)


def normalized(name):
    name = name.replace('|', '_')
    return re.sub(r'-[0-9a-fA-F]{6,10}$', '', name) if name.startswith('sactx-') else name


def scan_metadata(game, cache):
    import UnityPy
    UnityPy.config.FALLBACK_UNITY_VERSION = UNITY_VERSION
    textures, materials, collections = {}, {}, []
    root = game / 'StreamingAssets/aa/StandaloneOSX'
    def ref(af, pointer):
        cab = af.name if pointer['m_FileID'] == 0 else af.externals[pointer['m_FileID']-1].path.rsplit('/', 1)[-1]
        return cab + ':' + str(pointer['m_PathID'])
    paths = sorted(root.rglob('*.bundle'))
    for i, path in enumerate(paths):
        env = UnityPy.load(str(path))
        for obj in env.objects:
            af = obj.assets_file
            key = af.name + ':' + str(obj.path_id)
            if obj.type.name == 'Texture2D':
                tex = obj.read()
                textures[key] = dict(bundle=path.relative_to(root).as_posix(), cab=af.name,
                    path_id=obj.path_id, name=tex.m_Name, width=tex.m_Width, height=tex.m_Height,
                    mips=tex.m_MipCount, readable=tex.m_IsReadable)
            elif obj.type.name == 'Material':
                tree = obj.read_typetree()
                for name, prop in tree['m_SavedProperties']['m_TexEnvs']:
                    if name == '_MainTex' and prop['m_Texture']['m_PathID']:
                        materials[key] = ref(af, prop['m_Texture'])
            elif obj.type.name == 'MonoBehaviour' and obj.serialized_type.node and any(
                    n.m_Name == 'spriteCollectionName' for n in obj.serialized_type.node.m_Children):
                tree = obj.read_typetree()
                if 'spriteCollectionName' not in tree:
                    continue
                name = af.objects[tree['m_GameObject']['m_PathID']].read_typetree()['m_Name']
                collections.append(dict(name=name, materials=[ref(af, p) for p in tree['materials']]))
        del env
        if i % 100 == 0:
            print(f'Skin metadata: {i}/{len(paths)} bundles', flush=True)
    result = dict(textures=textures, materials=materials, collections=collections)
    write_json(cache, result)
    return result


def match_skin(skin, metadata):
    from PIL import Image
    skin = skin.resolve()
    if not (skin / 'active.txt').is_file():
        raise ValueError('Select an active skin folder containing active.txt')
    pngs = {p.relative_to(skin).as_posix(): p for p in skin.rglob('*.png')}
    if not pngs:
        raise ValueError('No skin PNGs')
    textures = metadata['textures']
    selected, matched = {}, set()
    def add(key, relative):
        old = selected.get(key)
        if old is not None and old != relative:
            raise ValueError(f'Conflicting replacements for {key}: {old}, {relative}')
        selected[key] = relative
        matched.add(relative)
    # Component.name is its GameObject name, not m_Name on MonoBehaviour.
    for collection in metadata['collections']:
        for material in collection['materials']:
            key = metadata['materials'].get(material)
            if key not in textures:
                continue
            relative = collection['name'] + '/' + textures[key]['name'] + '.png'
            if relative in pngs:
                add(key, relative)
    names = defaultdict(list)
    for key, tex in textures.items():
        names[normalized(tex['name'])].append(key)
    for relative, path in pngs.items():
        if path.parent != skin / 'Texture2D':
            continue
        with Image.open(path) as image:
            size = image.size
        for key in names[normalized(path.stem)]:
            tex = textures[key]
            if size == (tex['width'], tex['height']):
                add(key, relative)
    missing = sorted(set(pngs) - matched)
    if missing:
        raise ValueError('Unmatched skin PNGs (wrong game version?): ' + ', '.join(missing))
    bundles = defaultdict(list)
    for key, relative in sorted(selected.items()):
        row = dict(metadata['textures'][key], png=str(pngs[relative]), skin_file=relative)
        bundles[row['bundle']].append(row)
    return dict(bundles), len(matched)


def overlay_bundle(job):
    """One bundle per worker bounds decoded image memory."""
    source, output, name, replacements = job
    import UnityPy
    import astc_encoder as astc
    from PIL import Image
    from UnityPy.export import Texture2DConverter as converter
    from .texture_conversion import (context, error_metrics, acceptable, mip_bytes,
        texture_payload, compact_texture_streams)
    if Path(source).is_file():
        with zipfile.ZipFile(source) as archive:
            payload = archive.read('SilksongResources-v1/aa/StandaloneOSX/' + name)
    else:
        payload = (Path(source) / 'SilksongResources-v1/aa/StandaloneOSX' / name).read_bytes()
    env = UnityPy.load(payload)
    bundle, = env.files.values()
    expected = {(r['cab'], r['path_id']): r for r in replacements}
    unchanged, textures, streams, changed = {}, {}, {}, []
    for cab, af in bundle.files.items():
        if not hasattr(af, 'objects'):
            continue
        if af._m_target_platform != 9:
            raise ValueError('Skin overlays require already converted iOS resources')
        for obj in af.objects.values():
            key = (cab, obj.path_id)
            if obj.type.name != 'Texture2D':
                unchanged[key] = obj.get_raw_data()
                continue
            tex = obj.read()
            textures[key] = tex
            streams[key] = {k: getattr(tex.m_StreamData, k) for k in ('path', 'offset', 'size')}
            if key not in expected:
                continue
            row = expected.pop(key)
            if tex.m_Name != row['name']:
                raise ValueError('Texture identity differs from source game')
            with Image.open(row['png']) as original:
                # Unity stores rows bottom-up. LoadImage performs this flip too.
                image = original.convert('RGBA').transpose(Image.Transpose.FLIP_TOP_BOTTOM)
            tex.m_Width, tex.m_Height = image.size
            mips, scores, encoded = [], [], bytearray()
            for _ in range(max(1, tex.m_MipCount or 1)):
                raw = image.tobytes()
                packed = context(tex.m_ColorSpace == 1).compress(
                    astc.ASTCImage(astc.ASTCType.U8, *image.size, 1, raw), astc.ASTCSwizzle.from_str('RGBA'))
                assert len(packed) == mip_bytes(54, *image.size)
                decoded = converter.parse_image_data(packed, *image.size, 54,
                    obj.version, 9, flip=False).convert('RGBA')
                scores.append(error_metrics(image, decoded))
                mips.append(raw)
                encoded.extend(packed)
                image = image.resize((max(1, image.width//2), max(1, image.height//2)), Image.Resampling.LANCZOS)
            good = not tex.m_IsReadable and all(acceptable(score) for score in scores)
            tex.image_data = bytes(encoded) if good else b''.join(mips)
            tex.m_TextureFormat = 54 if good else 4
            tex.m_CompleteImageSize = len(tex.image_data)
            tex.m_StreamData.path = ''
            tex.m_StreamData.size = tex.m_StreamData.offset = 0
            tex.save()
            changed.append(dict(skin_file=row['skin_file'], cab=cab, path_id=obj.path_id,
                width=tex.m_Width, height=tex.m_Height, format=tex.m_TextureFormat, scores=scores))
            del image, mips, encoded
    if expected:
        raise ValueError('Texture objects missing from converted pack')
    pixels = {key: texture_payload(tex) for key, tex in textures.items()}
    compact_texture_streams(bundle, textures, streams)
    target = Path(output) / name
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_bytes(bundle.save(packer='lz4'))
    check = UnityPy.load(str(target))
    objects = {(o.assets_file.name, o.path_id): o for o in check.objects}
    assert set(objects) == set(unchanged) | set(textures)
    for key, raw in unchanged.items():
        assert objects[key].get_raw_data() == raw, 'Non-texture object changed'
    for key, tex in textures.items():
        reread = objects[key].read()
        assert texture_payload(reread) == pixels[key], 'Texture serialization changed'
        assert (reread.m_Width, reread.m_Height, reread.m_TextureFormat) == (tex.m_Width, tex.m_Height, tex.m_TextureFormat)
    del env, bundle, check, textures, objects
    gc.collect()
    return dict(bundle=name, bytes=target.stat().st_size, textures=changed)
