"""Optional, build-time BepInEx plugins and compressed Customizer skin overlays."""
import hashlib
import json
import os
from pathlib import Path
import subprocess
import zipfile

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
    if (plan.get('schema') != 2 or not plan.get('plugins')
            or plan.get('contract', {}).get('schema') != 2
            or 'overrides' in plan['contract']):
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


def converted_index(resources):
    from .common import PACK_FOLDER, UNITY_VERSION, RESOURCE_REVISION
    from .resources import validate_index
    if resources.is_file():
        with zipfile.ZipFile(resources) as archive:
            index = json.loads(archive.read(PACK_FOLDER + '/resource-index.json'))
    else:
        index = read_json(resources / PACK_FOLDER / 'resource-index.json')
    validate_index(index)
    if not index['pack']['compatibility'].startswith(f'{UNITY_VERSION}:ios-r{RESOURCE_REVISION}:'):
        raise ValueError('Mods require converted iOS resources')
    return index


def prepare_mod_pack(args):
    from .common import output_directory
    from .texture_conversion import convert_bundle
    game, hpbar, resources = [p.expanduser().resolve() for p in
                             (args.game_data, args.hpbar, args.resources)]
    inputs_to_protect = [game, hpbar, resources]
    if getattr(args, 'skin', None):
        inputs_to_protect.append(args.skin)
    output = output_directory(args.output, *inputs_to_protect)
    if output.exists():
        raise ValueError('Use a fresh mod output directory')
    index = converted_index(resources)
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
    # Compiled plugin data only. No skin name, file list or size is baked in.
    contract = dict(schema=2, compatibility=index['pack']['compatibility'],
        files=[dict(path='plugins/Silk_HpBarPro/healthbar_pro', bytes=bundle.stat().st_size)])
    write_json(inputs / 'mod-plan.json', dict(schema=2, plugins=[dict(file='Silk_HpBarPro.dll',
        sha256=sha256(inputs / 'Silk_HpBarPro.dll'))], contract=contract))
    write_json(mods / 'mod-info.json', dict(schema=2, compatibility=contract['compatibility'],
        healthbar='HPBarPro 1.0.4', restart_required=True))
    archive_path = output / 'SilksongMods-hpbar-ios.zip'
    with zipfile.ZipFile(archive_path, 'w', zipfile.ZIP_DEFLATED, compresslevel=1) as archive:
        for path in sorted(mods.rglob('*')):
            if path.is_file():
                archive.write(path, path.relative_to(mods.parent).as_posix())
    write_json(output / 'mod-pack-report.json', dict(schema=2,
        bytes=archive_path.stat().st_size, sha256=sha256(archive_path), contract=contract,
        device_verified=False))
    print('Mod data: ' + str(archive_path))
    print('Build with: prepare --mods "' + str(inputs) + '" ...')
    if getattr(args, 'skin', None):
        from types import SimpleNamespace
        from .mod_skins import prepare_skin_pack
        prepare_skin_pack(SimpleNamespace(game_data=game, resources=resources, skin=args.skin,
            output=output / 'skin', jobs=args.jobs))
