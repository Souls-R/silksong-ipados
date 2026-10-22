"""Compile on Apple Silicon macOS with the existing Release ARM64 settings."""
import json
import platform
import plistlib
import shutil
import sys
import zipfile

from .common import REPO, UNITY_VERSION, output_directory, read_json, require_files, run, write_json
from .resources import validate_contract


def inspect_ipa(ipa, manifest):
    with zipfile.ZipFile(ipa) as archive:
        names = archive.namelist()
        prefix = 'Payload/SilksongNative.app/'
        info = plistlib.loads(archive.read(prefix + 'Info.plist'))
        if (info['CFBundleIdentifier'] != manifest['bundle_id']
                or info['CFBundleShortVersionString'] != manifest['version']
                or info['CFBundleVersion'] != manifest['build']):
            raise ValueError('IPA application identity differs from requested build')
        for name in ['Unity-iPhone', 'Frameworks/UnityFramework.framework/UnityFramework',
                     'Data/Managed/Metadata/global-metadata.dat', 'Data/Raw/native-resource-index.json']:
            if prefix + name not in names:
                raise ValueError('IPA missing ' + name)
        if any(n.startswith(prefix + 'Data/Raw/aa/StandaloneOSX/') for n in names):
            raise ValueError('Large resource bundles must stay in Documents')
        contract = validate_contract(json.loads(archive.read(prefix + 'Data/Raw/native-resource-index.json')))
        if contract != manifest['resource_contract']:
            raise ValueError('IPA resource contract differs')
        if manifest.get('mod_contract'):
            embedded = json.loads(archive.read(prefix + 'Data/Raw/native-mod-index.json'))
            if embedded != manifest['mod_contract']:
                raise ValueError('IPA mod contract differs')
            assemblies = json.loads(archive.read(prefix + 'Data/ScriptingAssemblies.json'))['names']
            if not {'BepInEx.dll', '0Harmony.dll'}.issubset(assemblies):
                raise ValueError('IPA missing compiled mod runtime assemblies')
        if manifest.get('launch_ui'):
            for name in ['Settings.bundle/Root.plist', 'Assets.car',
                         'Data/Raw/native-launch/strings.json',
                         'Data/Raw/native-launch/Warning_Fleur0008.png',
                         'Data/Raw/native-launch/main_menu_cursor_small0010.png',
                         'Data/Raw/native-launch/NotoSerifCJKsc-Regular.otf',
                         'Data/Raw/native-launch/Perpetua.otf']:
                if prefix + name not in names:
                    raise ValueError('IPA startup experience missing ' + name)
            for language in ['en', 'zh-Hans', 'zh-Hant']:
                for relative in [f'{language}.lproj/InfoPlist.strings',
                                 f'Settings.bundle/{language}.lproj/Root.strings']:
                    if prefix + relative not in names:
                        raise ValueError('IPA localization missing ' + relative)
            settings = plistlib.loads(archive.read(prefix + 'Settings.bundle/Root.plist'))
            keys = {item['Key'] for item in settings['PreferenceSpecifiers'] if 'Key' in item}
            if keys != {'NativeShowNotice', 'NativeRecheckResources', 'NativeDiagnosticLogs',
                        'NativeMetalHUD', 'NativeStartupDetails'}:
                raise ValueError('Unexpected startup settings')
    return dict(version=manifest['version'], build=manifest['build'], bytes=ipa.stat().st_size,
                resource_storage='documents', embedded_large_bundles=0, device_verified=False)


def build_native(args):
    if sys.platform != 'darwin' or platform.machine() != 'arm64':
        raise ValueError('Native compilation requires an Apple Silicon Mac with Xcode installed')
    work = output_directory(args.work)
    manifest = read_json(work / 'build.json')
    if manifest.get('schema') != 1 or manifest.get('unity') != UNITY_VERSION or not manifest.get('prepared'):
        raise ValueError('Not a completed native build directory')
    if 'resource_contract' not in manifest:
        raise ValueError('Prepared project uses the old resource format; rerun prepare')
    validate_contract(manifest['resource_contract'])
    project, il2cpp, logs = work / 'project', work / 'IL2CPP', work / 'logs'
    run(['ruby', '-e', "gem 'xcodeproj', '=1.27.0'; require 'xcodeproj'"], logs / 'ruby-check.log')
    # Catch project/localization configuration errors before the long C++ build.
    # The second pass below also links the static libraries once they exist.
    run(['ruby', REPO / 'scripts/configure_xcode.rb', project, manifest['bundle_id']],
        logs / 'configure-xcode-preflight.log')
    run(['xcodebuild', '-list', '-project', project / 'Unity-iPhone.xcodeproj'],
        logs / 'xcode-project-preflight.log', cwd=work)
    deploy = il2cpp / 'build/deploy_arm64'
    if not (deploy / 'il2cpp').is_file():
        deploy = il2cpp / 'build/deploy'
    require_files(deploy, ['il2cpp'])
    # Windows transfers lose executable bits. Check file headers, not hashes.
    for file in deploy.rglob('*'):
        if file.is_file():
            with file.open('rb') as stream:
                magic = stream.read(4)
            if magic in (b'\xcf\xfa\xed\xfe', b'\xca\xfe\xba\xbe', b'\xfe\xed\xfa\xcf') or magic.startswith(b'#!'):
                file.chmod(file.stat().st_mode | 0o111)
    output = work / 'native-output'
    output.mkdir(exist_ok=True)
    cache = work / 'bee-cache'
    cache.mkdir(exist_ok=True)
    command = [deploy / 'il2cpp', '--compile-cpp', '--platform=iOS', '--architecture=arm64',
               '--configuration=Release', '--dotnetprofile=unityaot-macos', '--static-lib-il2-cpp',
               '--jobs=' + str(args.jobs), '--verbose', '--custom-il2-cpp-root=' + str(il2cpp),
               '--baselib-directory=' + str(project / 'Libraries'),
               '--generatedcppdir=' + str(project / 'Il2CppOutput'),
               '--outputpath=' + str(output / 'libGameAssembly.a'), '--cachedirectory=' + str(cache)]
    run(command, logs / 'il2cpp-native.log', cwd=work)
    for name in ['libGameAssembly.a', 'il2cpp.a', 'baselib.a']:
        require_files(output, [name])
        shutil.copyfile(output / name, project / 'Libraries' / name)
    run([sys.executable, REPO / 'scripts/register_modules.py', '--project', project, '--logs', logs],
        logs / 'register-modules.log')
    run(['ruby', REPO / 'scripts/configure_xcode.rb', project, manifest['bundle_id']],
        logs / 'configure-xcode.log')
    run(['xcodebuild', '-project', project / 'Unity-iPhone.xcodeproj', '-scheme', 'Unity-iPhone',
         '-configuration', 'Release', '-sdk', 'iphoneos', '-destination', 'generic/platform=iOS',
         '-derivedDataPath', work / 'DerivedData', 'CODE_SIGNING_ALLOWED=NO',
         'CODE_SIGNING_REQUIRED=NO', 'CODE_SIGN_IDENTITY=', 'build'], logs / 'xcodebuild.log', cwd=work)
    app = work / 'DerivedData/Build/Products/Release-iphoneos/Unity-iPhone.app'
    require_files(app, ['Unity-iPhone', 'Frameworks/UnityFramework.framework/UnityFramework'])
    run(['xcrun', 'lipo', '-info', app / 'Frameworks/UnityFramework.framework/UnityFramework'],
        logs / 'architecture.txt')
    if 'arm64' not in (logs / 'architecture.txt').read_text():
        raise ValueError('Expected an ARM64 native framework')
    package = work / 'package/Payload/SilksongNative.app'
    if package.exists():
        if not package.resolve().is_relative_to(work) or package.is_symlink():
            raise ValueError('Unsafe package output directory')
        shutil.rmtree(package)
    package.parent.mkdir(parents=True, exist_ok=True)
    run(['ditto', app, package], logs / 'package-copy.log')
    destination = work / 'output'
    destination.mkdir(exist_ok=True)
    ipa = destination / f'SilksongNative-{manifest["version"]}-unsigned.ipa'
    temporary = destination / 'program.partial.zip'
    if temporary.exists():
        temporary.unlink()
    run(['/usr/bin/zip', '-qry', temporary, 'Payload'], logs / 'package-zip.log', cwd=work / 'package')
    report = inspect_ipa(temporary, manifest)
    temporary.replace(ipa)
    write_json(destination / 'package-report.json', report)
    manifest['native_built'] = True
    write_json(work / 'build.json', manifest)
    print(f'Unsigned IPA: {ipa}')
