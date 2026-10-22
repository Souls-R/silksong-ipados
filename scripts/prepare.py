"""Create the entire native project from the user's game and official tools."""
from pathlib import Path
import json
import os
import plistlib
import re
import shutil
import sys

from .common import (PACK_FOLDER, REPO, UNITY_VERSION, output_directory,
                     read_json, require_files, run, write_json)
from .resources import Catalog, catalog_bundles, compatibility_id, resource_contract, validate_index

STARTUP_FILES = ['globalgamemanagers', 'globalgamemanagers.assets', 'resources.assets',
                 'level0', 'sharedassets0.assets', 'Resources/unity_builtin_extra']


def ios_support(unity):
    roots = [unity]
    if unity.name == 'Contents' and unity.parent.suffix == '.app':
        # macOS installs optional modules beside Unity.app, outside its signature.
        roots.insert(0, unity.parent.parent)
    for root in roots:
        candidate = root / 'PlaybackEngines/iOSSupport'
        if candidate.is_dir():
            return candidate
    raise ValueError(f'iOS Build Support is not installed for this Unity Editor: {unity}')


def stage_managed(game, unity, destination):
    bcl = unity / 'MonoBleedingEdge/lib/mono/unityaot-macos'
    engine = ios_support(unity) / 'Variations/il2cpp/Releasearm64_managed'
    destination.mkdir(parents=True)
    for folder in (game / 'Managed', bcl, bcl / 'Facades', engine):
        files = sorted(folder.glob('*.dll'))
        if not files:
            raise ValueError(f'No assemblies in {folder}')
        for source in files:
            shutil.copyfile(source, destination / source.name)
    require_files(destination, ['Assembly-CSharp.dll', 'UnityEngine.CoreModule.dll',
                                'Unity.InputSystem.dll', 'Unity.ResourceManager.dll'])


def tool(unity, relative):
    candidate = unity / relative
    if os.name == 'nt':
        candidate = candidate.with_suffix('.exe')
    if not candidate.is_file():
        raise ValueError(f'Tool not installed: {candidate}')
    return candidate


def compile_helper(unity, package, managed, work, diagnostics):
    package_info = read_json(package / 'package.json')
    if package_info.get('name') != 'com.unity.inputsystem' or package_info.get('version') != '1.14.2':
        raise ValueError('Input System package must be the official 1.14.2 package')
    source = package / 'InputSystem/Plugins/iOS/IOSGameController.cs'
    require_files(package, [source.relative_to(package).as_posix(), 'LICENSE.md'])
    args = ['-nologo', '-target:library', '-nostdlib+', '-optimize+', '-unsafe+',
            '-define:UNITY_IOS',
            f'-out:"{managed / "NativeCompatibility.dll"}"']
    args += [f'-r:"{p}"' for p in sorted(managed.glob('*.dll'))]
    args += [f'"{p}"' for p in sorted((REPO / 'src/runtime').glob('*.cs'))]
    args.append(f'"{source}"')
    response = work / 'helper.rsp'
    response.write_text('\n'.join(args), encoding='utf-8')
    run([tool(unity, 'NetCoreRuntime/dotnet'), unity / 'DotNetSdkRoslyn/csc.dll', '@' + str(response)],
        work / 'logs/helper.log')


def startup_assets(game, ios, data):
    import UnityPy
    UnityPy.config.FALLBACK_UNITY_VERSION = UNITY_VERSION
    data.mkdir(parents=True)
    for name in STARTUP_FILES:
        env = UnityPy.load(str(game / name))
        af, = env.files.values()
        if name == 'level0':
            loaders = 0
            for obj in af.objects.values():
                if obj.type.name == 'GameObject':
                    tree = obj.read_typetree()
                    if tree['m_Name'] == 'Loader':
                        tree['m_IsActive'] = False
                        obj.save_typetree(tree)
                        loaders += 1
            if loaders != 1:
                raise ValueError('Unsupported bootstrap scene: expected one Loader')
        if name == 'globalgamemanagers':
            count = 0
            for obj in af.objects.values():
                if obj.type.name == 'BuildSettings':
                    tree = obj.read_typetree()
                    tree['m_Version'] = UNITY_VERSION
                    tree['m_GraphicsAPIs'] = [16]
                    obj.save_typetree(tree)
                    count += 1
            if count != 1:
                raise ValueError('Unsupported game BuildSettings')
        af.unity_version = UNITY_VERSION
        af._m_target_platform = 9
        af.mark_changed()
        target = data / name
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(af.save())
    for source in game.glob('*.resS'):
        shutil.copyfile(source, data / source.name)
    shutil.copyfile(ios / 'Data/Resources/unity default resources', data / 'Resources/unity default resources')
    shutil.copyfile(game / 'app.info', data / 'app.info')
    (data / 'boot.config').write_text(
        'gfx-threading-mode=4\nwait-for-native-debugger=0\nhdr-display-enabled=0\nscripting-runtime-version=latest\n',
        encoding='utf-8')
    assemblies = read_json(game / 'ScriptingAssemblies.json')
    if len(assemblies['names']) != len(assemblies['types']):
        raise ValueError('Invalid scripting assemblies list')
    assemblies['names'].append('NativeCompatibility.dll')
    assemblies['types'].append(assemblies['types'][assemblies['names'].index('Assembly-CSharp.dll')])
    write_json(data / 'ScriptingAssemblies.json', assemblies)
    callbacks = read_json(game / 'RuntimeInitializeOnLoads.json')
    callbacks['root'] += [dict(assemblyName='NativeCompatibility', nameSpace='',
                              className='NativeBootstrap', methodName=name,
                              loadTypes=kind, isUnityClass=False)
                          for name, kind in [('Install', 3), ('Boot', 0)]]
    write_json(data / 'RuntimeInitializeOnLoads.json', callbacks)


def prepare(args):
    game = args.game_data.expanduser().resolve()
    unity = args.unity_data.expanduser().resolve()
    package = args.input_system.expanduser().resolve()
    resources = args.resources.expanduser().resolve()
    output = output_directory(args.output, game, unity, package, resources)
    if output.exists():
        raise ValueError('Use a fresh output directory; preparation never overwrites an existing build')
    if not re.fullmatch(r'[A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)+', args.bundle_id):
        raise ValueError('Invalid application bundle identifier')
    if not re.fullmatch(r'\d+\.\d+\.\d+', args.version) or not args.build_number.isdigit():
        raise ValueError('Use a three-part numeric version and an integer build number')
    require_files(game, STARTUP_FILES + ['app.info', 'ScriptingAssemblies.json',
                         'RuntimeInitializeOnLoads.json', 'Managed/Assembly-CSharp.dll'])
    ios = ios_support(unity)
    require_files(ios, ['Trampoline/Unity-iPhone.xcodeproj/project.pbxproj',
                       'Trampoline/Libraries/libiPhone-lib.a', 'Data/Resources/unity default resources'])
    require_files(unity, ['DotNetSdkRoslyn/csc.dll'])
    require_files(package, ['package.json', 'LICENSE.md', 'InputSystem/Plugins/iOS/IOSGameController.cs'])
    index = validate_index(read_json(resources / PACK_FOLDER / 'resource-index.json'),
                           resources / PACK_FOLDER / 'aa/StandaloneOSX')
    if read_json(resources / PACK_FOLDER / 'pack.json') != index['pack']:
        raise ValueError('Resources are incomplete')
    if set(catalog_bundles(Catalog(game / 'StreamingAssets/aa/catalog.bin'))) != {r['bundle'] for r in index['bundles']}:
        raise ValueError('Resource pack does not match this game catalog')
    import UnityPy
    env = UnityPy.load(str(game / 'globalgamemanagers'))
    af, = env.files.values()
    if af.unity_version not in (UNITY_VERSION, UNITY_VERSION + '-uum-100966-branch1'):
        raise ValueError(f'Unsupported game Unity version: {af.unity_version}; expected {UNITY_VERSION}')
    # Small catalog comparison prevents accidental reuse across game updates.
    from .resources import adapt_catalog
    output.mkdir(parents=True)
    adapt_catalog(game / 'StreamingAssets/aa/catalog.bin', output / 'catalog-check')
    if (output / 'catalog-check/catalog.bin').read_bytes() != (resources / 'program/Raw/aa/catalog.bin').read_bytes():
        raise ValueError('Resource catalog was prepared from different game files')
    if index['pack']['compatibility'] != compatibility_id((output / 'catalog-check/catalog.hash').read_text().strip()):
        raise ValueError('Resource compatibility version does not match this game catalog')
    contract = resource_contract(index)
    managed = output / 'managed'
    stage_managed(game, unity, managed)
    compile_helper(unity, package, managed, output, args.diagnostics)
    project = output / 'project'
    shutil.copytree(ios / 'Trampoline', project, ignore=shutil.ignore_patterns('*sim*', '*-dev.a'))
    shutil.copytree(ios / 'il2cpp', output / 'IL2CPP', ignore=shutil.ignore_patterns('deploy_x86_64', '*.pdb'))
    cpp = project / 'Il2CppOutput'
    command = [tool(unity, 'il2cpp/build/deploy/il2cpp'), '--convert-to-cpp', '--platform=iOS',
               '--architecture=arm64', '--dotnetprofile=unityaot-macos', '--static-lib-il2-cpp',
               '--directory=' + str(managed), '--generatedcppdir=' + str(cpp),
               '--data-folder=' + str(output / 'metadata'), '--emit-null-checks',
               '--enable-array-bounds-check', '--enable-stacktrace', '--jobs=4']
    run(command, output / 'logs/il2cpp-convert.log', cwd=output)
    run([sys.executable, REPO / 'scripts/patch_generated_compat.py', '--cpp', cpp],
        output / 'logs/patches.log', cwd=output)
    preprocessor = project / 'Classes/Preprocessor.h'
    source = preprocessor.read_text(encoding='utf-8')
    if '#define PLATFORM_IOS    0' not in source:
        raise ValueError('Unexpected official iOS template version')
    preprocessor.write_text(source.replace('#define PLATFORM_IOS    0', '#define PLATFORM_IOS    1')
                           .replace('#define UNITY_USES_IAD 1', '#define UNITY_USES_IAD 0'), encoding='utf-8')
    data = project / 'Data'
    startup_assets(game, ios, data)
    shutil.copytree(output / 'metadata', data / 'Managed')
    shutil.copytree(resources / 'program/Raw', data / 'Raw')
    write_json(data / 'Raw/native-resource-index.json', contract)
    for source in (REPO / 'src/runtime').iterdir():
        if source.suffix in ('.mm', '.h'):
            shutil.copyfile(source, project / 'Libraries' / source.name)
    notices = data / 'ThirdPartyNotices'
    notices.mkdir()
    shutil.copyfile(package / 'LICENSE.md', notices / 'Unity-InputSystem-LICENSE.md')
    for name in ('Third Party Notices.md', 'Third Party Notices.txt'):
        if (package / name).is_file():
            shutil.copyfile(package / name, notices / name)
    shutil.copyfile(REPO / 'LICENSE', notices / 'Builder-LICENSE.txt')
    info_path = project / 'Info.plist'
    info = plistlib.loads(info_path.read_bytes())
    # The template carries per-device orientation keys and a device key always
    # wins over the shared one, so write all three: a phone has to stay in
    # landscape too instead of rotating the game into portrait.
    landscape = ['UIInterfaceOrientationLandscapeLeft', 'UIInterfaceOrientationLandscapeRight']
    orientations = {key: landscape for key in ('UISupportedInterfaceOrientations',
                                              'UISupportedInterfaceOrientations~iphone',
                                              'UISupportedInterfaceOrientations~ipad')}
    info.update(CFBundleDisplayName=args.app_name, CFBundleShortVersionString=args.version,
                CFBundleVersion=args.build_number, UIFileSharingEnabled=True,
                LSSupportsOpeningDocumentsInPlace=True, UIRequiresFullScreen=True)
    info.update(orientations)
    info_path.write_bytes(plistlib.dumps(info))
    from .mouse import configure_mouse
    configure_mouse(project)
    from .launch import configure
    configure(project, game, args.diagnostics, args.metal_hud)
    manifest = dict(schema=1, unity=UNITY_VERSION, bundle_id=args.bundle_id, app_name=args.app_name,
                    version=args.version, build=args.build_number, diagnostics=args.diagnostics,
                    metal_hud=args.metal_hud, resource_contract=contract, launch_ui=True,
                    prepared=True, native_built=False)
    write_json(output / 'build.json', manifest)
    print(f'Prepared complete native build: {output}\nNext, on macOS: python3 build.py native --work "{output}"')
