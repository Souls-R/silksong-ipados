"""Route original game display API calls to the iOS boundary; retain menu logic."""
import re

METHOD = re.compile(r'^IL2CPP_EXTERN_C IL2CPP_METHOD_ATTR [^\n]+? (\w+) \([^\n]+\)\s*\n\{', re.M)


def apply(cpp):
    helper = (cpp / 'NativeCompatibility.cpp').read_text(encoding='utf-8')
    helpers = {}
    for match in METHOD.finditer(helper):
        name = match[1]
        if name.startswith('NativeDisplaySettings_'):
            simple = name[len('NativeDisplaySettings_'):].rsplit('_m', 1)[0]
            helpers[simple] = (name, match[0].rstrip()[:-1].rstrip() + ';')
    routes = {
        'Screen_get_resolutions': 'GetResolutions',
        'Screen_get_currentResolution': 'GetCurrentResolution',
        'Screen_SetResolution_m8EDEB77BD1220D9836020BA4FA26D77A10771100': 'SetResolution',
        'Screen_SetResolution_mCDABC3299024A96B6ECD72B0859BAF424A0E4F7E': 'SetResolutionWithRate',
        'Screen_SetResolution_m6E0A043025530BAFC593251727AA3D8F8A9A3DB4': 'SetResolutionWithMode',
        'Application_set_targetFrameRate': 'SetTargetFrameRate',
        'Application_get_targetFrameRate': 'GetTargetFrameRate',
    }
    hooks = {
        'GameSettings_LoadVideoSettings': ('LoadResolution', 'NULL'),
        'MenuResolutionSetting_ApplySettings': ('BeginResolutionPreview', 'NULL'),
        'MenuResolutionSetting_RollbackResolution': ('RollbackResolutionPreview', 'NULL'),
        'UIManager_GoToVideoMenu': ('ReturnToVideoMenu', '___0_rollbackRes, NULL'),
    }
    counts = {key: 0 for key in routes}
    hooked = {key: 0 for key in hooks}
    changes = []
    aspect_count = reset_count = 0
    for path in cpp.glob('Assembly-CSharp*.cpp'):
        source = path.read_text(encoding='utf-8')
        used, edits = set(), []
        for match in METHOD.finditer(source):
            end = source.index('\n}', match.end()) + 2
            original = source[match.start():end]
            body = original
            name = match[1].rsplit('_m', 1)[0]
            for engine, boundary in routes.items():
                pattern = r'\b' + engine + (r'\b' if '_m' in engine else r'_m[0-9A-F]+\b') + r'(?=\()'
                if name == 'MenuResolutionSetting_ResetToDefaultResolution' and boundary == 'SetResolution':
                    boundary = 'ResetResolution'
                body, count = re.subn(pattern, helpers[boundary][0], body)
                counts[engine] += count
                if count:
                    used.add(boundary)
                    if boundary == 'ResetResolution': reset_count += count
            if name in hooks:
                boundary, arguments = hooks[name]
                opening = body.index('\n{') + 2
                body = body[:opening] + '\n\t' + helpers[boundary][0] + '(' + arguments + ');' + body[opening:]
                used.add(boundary)
                hooked[name] += 1
            if name == 'ForceCameraAspect_AutoScaleViewportShared':
                assert body.count('(1.60000002f), (2.39166665f)') == 1
                # Extend the original common camera/HUD/FOV calculation to 4:3
                # tablets. Preserve overscan, wide-screen cap, events and movies.
                body = body.replace('(1.60000002f), (2.39166665f)', '(1.33333337f), (2.39166665f)')
                aspect_count += 1
            if body != original:
                edits.append((match.start(), end, body))
                changes.append({'file': path.name, 'method': name})
        if edits:
            for start, end, body in reversed(edits):
                source = source[:start] + body + source[end:]
            insertion = METHOD.search(source).start()
            declarations = '\n// Original UI -> iOS display boundary.\n' + '\n'.join(helpers[key][1] for key in sorted(used)) + '\n'
            source = source[:insertion] + declarations + source[insertion:]
            path.write_text(source, encoding='utf-8', newline='\n')
    assert all(value > 0 for value in counts.values()), counts
    assert all(value == 1 for value in hooked.values()), hooked
    assert aspect_count == reset_count == 1
    return dict(routes=counts, hooks=hooked, camera_min_aspect='4:3 (iOS build only)',
                changed_methods=changes, original_menu_flow_retained=True,
                original_game_il_unchanged=True, runtime_verified=False)
