"""Optional, build-time BepInEx plugins and compressed Customizer skin overlays."""
import hashlib
import json
import os
from pathlib import Path
import subprocess
import zipfile
from concurrent.futures import ProcessPoolExecutor

from .common import REPO, run, read_json, write_json


def sha256(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def compile_shims(compiler, managed, work):
    vendor = REPO / 'third_party/silksong-android/bepinex-shim'
    for name, sources in (
        ('0Harmony', sorted((vendor / 'harmony').glob('*.cs'))),
        ('BepInEx', sorted((vendor / 'bepinex').glob('*.cs')) + [REPO / 'src/mods/Paths.cs']),
    ):
        flags = ['-nologo', '-target:library', '-nostdlib+', '-optimize+', '-langversion:latest',
                 f'-out:"{managed / (name + ".dll")}"']
        flags += [f'-r:"{p}"' for p in sorted(managed.glob('*.dll'))]
        flags += [f'"{p}"' for p in sources]
        response = work / (name + '.rsp')
        response.write_text('\n'.join(flags), encoding='utf-8')
        run([*compiler, '@' + str(response)], work / 'logs' / (name + '.log'))


def weave_plugins(source, managed, work, compiler, dotnet='dotnet', weaver=None):
    source = source.resolve()
    plan = read_json(source / 'mod-plan.json')
    if plan.get('schema') != 1 or not plan.get('plugins'):
        raise ValueError('Invalid mod plan')
    compile_shims(compiler, managed, work)
    project = REPO / 'tools/ModWeaver/ModWeaver.csproj'
    env = os.environ.copy()
    env.update(DOTNET_CLI_TELEMETRY_OPTOUT='1', DOTNET_GENERATE_ASPNET_CERTIFICATE='false',
               DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1')
    # Only this small tool uses the host SDK. Plugin DLLs are never executed.
    if weaver:
        command = [str(weaver.resolve())]
    else:
        binary = work / 'mod-weaver/bin'
        intermediate = work / 'mod-weaver/obj'
        subprocess.run([dotnet, 'build', str(project), '-c', 'Release', '--nologo', '-v', 'quiet',
                        '-o', str(binary), '-p:BaseIntermediateOutputPath=' + str(intermediate) + os.sep],
                       env=env, check=True)
        command = [dotnet, str(binary / 'ModWeaver.dll')]
    plugins = []
    for record in plan['plugins']:
        path = safe_path(source, record['file'])
        if sha256(path) != record['sha256']:
            raise ValueError('Plugin input changed: ' + str(path))
        patched = work / 'mod-inputs' / path.name
        run([*command, 'ios-compat', managed, path, patched],
            work / 'logs' / (path.stem + '-compat.log'))
        plugins += ['--mod', str(patched)]
    report = work / 'mod-weave-report.json'
    run([*command, 'weave', '--assemblies', managed, '--report', report, *plugins],
        work / 'logs/mod-weave.log')
    rows = [{k.lower(): v for k, v in row.items()} for row in read_json(report)['plugins']]
    # Partial patches must be explicitly reviewed rather than shipped silently.
    if len(rows) != len(plan['plugins']) or any(row['status'] != 'Ok' for row in rows):
        raise ValueError('Mod weaving incomplete; see ' + str(report))
    return plan['contract'], ['0Harmony.dll', 'BepInEx.dll'] + [r['assembly'] + '.dll' for r in rows]


def safe_path(root, relative):
    if not isinstance(relative, str) or '\\' in relative or ':' in relative or '\0' in relative:
        raise ValueError('Invalid mod path')
    parts = relative.split('/')
    if any(p in ('', '.', '..') for p in parts):
        raise ValueError('Invalid mod path')
    result = root.joinpath(*parts).resolve()
    if not result.is_relative_to(root.resolve()):
        raise ValueError('Mod path escapes root')
    return result


def prepare_mod_pack(args, metadata=None):
    from .common import PACK_FOLDER, output_directory
    from .mod_skins import scan_metadata, match_skin, overlay_bundle
    from .texture_conversion import convert_bundle
    game, skin, hpbar, resources = [p.expanduser().resolve() for p in
                                  (args.game_data, args.skin, args.hpbar, args.resources)]
    output = output_directory(args.output, game, skin, hpbar, resources)
    if output.exists():
        raise ValueError('Use a fresh mod output directory')
    if resources.is_file():
        with zipfile.ZipFile(resources) as archive:
            index = json.loads(archive.read(PACK_FOLDER + '/resource-index.json'))
    else:
        index = read_json(resources / PACK_FOLDER / 'resource-index.json')
    if ':ios-r2:' not in index['pack']['compatibility']:
        raise ValueError('Mods require converted iOS resources')
    output.mkdir(parents=True)
    inputs = output / 'inputs'
    inputs.mkdir()
    for name in ('Silk_HpBarPro.dll', 'healthbar_pro'):
        if hpbar.is_file():
            with zipfile.ZipFile(hpbar) as archive:
                matches = [p for p in archive.namelist() if p.rsplit('/', 1)[-1] == name]
                if len(matches) != 1:
                    raise ValueError('Expected exactly one HPBarPro file: ' + name)
                data = archive.read(matches[0])
        else:
            data = (hpbar / name).read_bytes()
        (inputs / name).write_bytes(data)
    mods = output / 'data/Mods'
    bundle = mods / 'plugins/Silk_HpBarPro/healthbar_pro'
    convert_bundle(inputs / 'healthbar_pro', bundle, output / 'hpbar-conversion.json')
    metadata = metadata if metadata is not None else scan_metadata(game, output / 'skin-metadata.json')
    selections, pngs = match_skin(skin, metadata)
    known = {r['bundle'] for r in index['bundles']}
    if set(selections) - known:
        raise ValueError('Skin textures do not match converted resource catalog')
    jobs = [(str(resources), str(mods / 'overrides'), name, rows) for name, rows in sorted(selections.items())]
    results = []
    with ProcessPoolExecutor(max_workers=args.jobs) as pool:
        for row in pool.map(overlay_bundle, jobs):
            results.append(row)
            print(f'Skin: {len(results)}/{len(jobs)} bundles ({row["bundle"]})', flush=True)
    write_json(output / 'skin-conversion.json', dict(pngs=pngs, bundles=results))
    # The exact data contract travels in the IPA. Files contain no executable DLLs.
    contract = dict(schema=1, compatibility=index['pack']['compatibility'],
        files=[dict(path='plugins/Silk_HpBarPro/healthbar_pro', bytes=bundle.stat().st_size)],
        overrides=[dict(bundle=r['bundle'], bytes=r['bytes']) for r in results])
    write_json(inputs / 'mod-plan.json', dict(schema=1, plugins=[dict(file='Silk_HpBarPro.dll',
        sha256=sha256(inputs / 'Silk_HpBarPro.dll'))], contract=contract))
    write_json(mods / 'mod-info.json', dict(schema=1, compatibility=contract['compatibility'],
        skin=skin.name, skin_pngs=pngs, healthbar='HPBarPro 1.0.4', restart_required=True))
    archive_path = output / 'SilksongMods-skin-hpbar-ios.zip'
    with zipfile.ZipFile(archive_path, 'w', zipfile.ZIP_DEFLATED, compresslevel=1) as archive:
        for path in sorted(mods.rglob('*')):
            if path.is_file():
                archive.write(path, path.relative_to(mods.parent).as_posix())
    write_json(output / 'mod-pack-report.json', dict(schema=1, skin_pngs=pngs,
        skin_textures=sum(len(r['textures']) for r in results), skin_bundles=len(results),
        bytes=archive_path.stat().st_size, sha256=sha256(archive_path), contract=contract,
        device_verified=False))
    print('Mod data: ' + str(archive_path))
    print('Build with: prepare --mods "' + str(inputs) + '" ...')
