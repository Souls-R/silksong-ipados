"""Generate native startup UI assets and Settings.bundle from user-owned input.

The repository contains original code and text only. Original artwork/fonts
are extracted during preparation and never read from a research/payload folder.
"""
import json
import plistlib
from pathlib import Path
from .common import REPO, require_files, write_json


def extract_assets(game, output):
    import UnityPy
    from PIL import Image
    bundles = game / 'StreamingAssets/aa/StandaloneOSX'
    sources = {
        'atlases_assets_assets/sprites/_atlases/titlefleurs.spriteatlas.bundle': {'Warning_Fleur0008'},
        'hud_assets_all.bundle': {'main_menu_cursor_small0010'},
        'fonts_assets_chinese.bundle': {'NotoSerifCJKsc-Regular'},
        'fonts_assets_english.bundle': {'Perpetua'},
    }
    for bundle, names in sources.items():
        require_files(bundles, [bundle])
        found = set()
        for obj in UnityPy.load(str(bundles / bundle)).objects:
            if obj.type.name not in ('Sprite', 'Font'):
                continue
            asset = obj.read()
            if asset.m_Name not in names:
                continue
            if asset.m_Name in found:
                raise ValueError('Ambiguous menu asset: ' + asset.m_Name)
            found.add(asset.m_Name)
            if obj.type.name == 'Font':
                (output / (asset.m_Name + '.otf')).write_bytes(bytes(asset.m_FontData))
            else:
                asset.image.save(output / (asset.m_Name + '.png'))
        if found != names:
            raise ValueError('Missing original menu assets: ' + str(names - found))
    icon = game.parent / 'PlayerIcon.icns'
    require_files(icon.parent, [icon.name])
    with Image.open(icon) as image:
        image.size = (1024, 1024)
        rgba = image.convert('RGBA')
    # iOS app icons must be opaque. Preserve the original image over black.
    return Image.alpha_composite(Image.new('RGBA', rgba.size, (0, 0, 0, 255)), rgba).convert('RGB')


def strings_file(values):
    return '\n'.join(json.dumps(key, ensure_ascii=False) + ' = ' +
                     json.dumps(value, ensure_ascii=False) + ';' for key, value in values.items()) + '\n'


def configure_launch_screen(project):
    # The system displays this before Unity or NativeLaunchView exists.
    # An empty UILaunchScreen falls back to white in light appearance.
    name = 'NativeLaunchBackground'
    colors = project / 'NativeIcons.xcassets' / (name + '.colorset')
    colors.mkdir(parents=True, exist_ok=True)
    write_json(colors / 'Contents.json', {
        'info': {'version': 1, 'author': 'xcode'},
        'colors': [{'idiom': 'universal', 'color': {
            'color-space': 'srgb',
            'components': {'red': '0.000', 'green': '0.000', 'blue': '0.000', 'alpha': '1.000'},
        }}],
    })
    info_path = project / 'Info.plist'
    info = plistlib.loads(info_path.read_bytes())
    for key in ('UILaunchStoryboardName', 'UILaunchStoryboardName~ipad', 'UILaunchScreens'):
        info.pop(key, None)
    info['UILaunchScreen'] = {'UIColorName': name}
    info_path.write_bytes(plistlib.dumps(info))


def configure(project, game, diagnostics=False, metal_hud=False):
    data = project / 'Data/Raw/native-launch'
    data.mkdir(parents=True)
    strings = json.loads((REPO / 'src/launch/strings.json').read_text(encoding='utf-8'))
    write_json(data / 'strings.json', strings)
    icon = extract_assets(game, data)
    catalog = project / 'NativeIcons.xcassets'
    icons = catalog / 'AppIcon.appiconset'
    icons.mkdir(parents=True)
    write_json(catalog / 'Contents.json', {'info': {'version': 1, 'author': 'xcode'}})
    images = []
    for idiom, sizes in [('iphone', [(20, 2), (20, 3), (29, 2), (29, 3), (40, 2), (40, 3), (60, 2), (60, 3)]),
                         ('ipad', [(20, 1), (20, 2), (29, 1), (29, 2), (40, 1), (40, 2), (76, 1), (76, 2), (83.5, 2)]),
                         ('ios-marketing', [(1024, 1)])]:
        for size, scale in sizes:
            name = f'{idiom}-{size}-{scale}.png'
            from PIL import Image
            icon.resize((int(size * scale),) * 2, Image.Resampling.LANCZOS).save(icons / name)
            images.append(dict(idiom=idiom, size=f'{size}x{size}', scale=f'{scale}x', filename=name))
    write_json(icons / 'Contents.json', dict(images=images, info={'version': 1, 'author': 'xcode'}))
    configure_launch_screen(project)
    settings = project / 'Settings.bundle'
    settings.mkdir()
    specifiers = [dict(Type='PSGroupSpecifier', Title='sectionLaunch', FooterText='settingNoticeHelp')]
    switches = [('NativeShowNotice', 'settingNotice', True),
                ('NativeRecheckResources', 'settingCheck', False),
                ('NativeDiagnosticLogs', 'settingLogs', diagnostics),
                ('NativeMetalHUD', 'settingHud', metal_hud),
                ('NativeStartupDetails', 'settingDetails', diagnostics)]
    for key, title, default in switches:
        if key == 'NativeRecheckResources':
            specifiers.append(dict(Type='PSGroupSpecifier', FooterText='settingCheckHelp'))
        if key == 'NativeDiagnosticLogs':
            specifiers.append(dict(Type='PSGroupSpecifier', Title='sectionDiagnostics', FooterText='diagnosticsHelp'))
        specifiers.append(dict(Type='PSToggleSwitchSpecifier', Title=title, Key=key, DefaultValue=default))
    (settings / 'Root.plist').write_bytes(plistlib.dumps(dict(PreferenceSpecifiers=specifiers, StringsTable='Root')))
    (settings / 'Info.plist').write_bytes(plistlib.dumps(dict(CFBundleIdentifier='native.settings', CFBundleVersion='1', CFBundlePackageType='BNDL')))
    for language, values in strings.items():
        lproj = project / (language + '.lproj')
        lproj.mkdir()
        (lproj / 'InfoPlist.strings').write_text(strings_file({'CFBundleDisplayName': values['appName'], 'CFBundleName': values['appName']}), encoding='utf-8')
        localized = settings / (language + '.lproj')
        localized.mkdir()
        labels = {key: value for key, value in values.items() if key.startswith(('setting', 'section'))}
        labels['diagnosticsHelp'] = values['settingLogsHelp']
        (localized / 'Root.strings').write_text(strings_file(labels), encoding='utf-8')
    # Defaults must be registered before loading Unity/Metal. Environment value
    # follows the persisted user choice, including turning a build default off.
    main = project / 'MainApp/main.mm'
    source = main.read_text(encoding='utf-8')
    entry = '        id ufw = UnityFrameworkLoad();'
    if source.count(entry) != 1:
        raise ValueError('Expected one UnityFramework entry')
    startup = '''        // Register Settings.bundle defaults without overwriting saved choices.
        NSUserDefaults* prefs = NSUserDefaults.standardUserDefaults;
        NSString* root = [[NSBundle mainBundle] pathForResource:@"Root" ofType:@"plist" inDirectory:@"Settings.bundle"];
        NSDictionary* definition = [NSDictionary dictionaryWithContentsOfFile:root];
        NSMutableDictionary* defaults = [NSMutableDictionary dictionary];
        for (NSDictionary* item in definition[@"PreferenceSpecifiers"])
            if (item[@"Key"] && item[@"DefaultValue"]) defaults[item[@"Key"]] = item[@"DefaultValue"];
        [prefs registerDefaults:defaults];
        setenv("MTL_HUD_ENABLED", [prefs boolForKey:@"NativeMetalHUD"] ? "1" : "0", 1);
        setenv("MTL_HUD_ALIGNMENT", "bottomright", 1);
        setenv("MTL_HUD_SCALE", "0.20", 1);
        setenv("MTL_HUD_ELEMENTS", "fps,frameinterval,gputime,frameintervalgraph", 1);
        setenv("MTL_HUD_LOGGING_ENABLED", "0", 1);
'''
    main.write_text('#include <stdlib.h>\n' + source.replace(entry, startup + entry), encoding='utf-8')
    info_path = project / 'Info.plist'
    info = plistlib.loads(info_path.read_bytes())
    info.update(CFBundleDisplayName=strings['en']['appName'], CFBundleName=strings['en']['appName'],
                CFBundleDevelopmentRegion='en', CFBundleLocalizations=list(strings))
    info.pop('MetalHudEnabled', None)
    info_path.write_bytes(plistlib.dumps(info))
