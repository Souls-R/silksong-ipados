"""Resolve iOS controller identity before controls and original game bindings."""
from patch_original_display import METHOD
import re


def method(cpp, prefix, name):
    matches = []
    for path in cpp.glob(prefix + '*.cpp'):
        source = path.read_text(encoding='utf-8')
        for match in METHOD.finditer(source):
            if match[1].rsplit('_m', 1)[0] == name:
                end = source.index('\n}', match.end()) + 2
                matches.append((path, source, match, end))
    assert len(matches) == 1, 'Expected one ' + name
    return matches[0]


def helper(cpp, name, parameter):
    _, _, match, _ = method(cpp, 'NativeCompatibility', 'NativeControllerIdentity_' + name)
    declaration = match[0].rstrip()[:-1].rstrip() + ';'
    argument = re.search(parameter, declaration)
    assert argument, 'Helper ABI changed: ' + name
    return match[1], declaration, argument


def apply(cpp):
    style, style_decl, style_arg = helper(cpp, 'DetectStyle', r'\b(InputDevice_t\w*)\*\s+___0_\w+')
    resolve, resolve_decl, resolve_arg = helper(cpp, 'ResolveGamepadType', r'\b(InputDevice_t\w*)\*\s+___0_\w+')
    prepare, prepare_decl, _ = helper(cpp, 'PrepareBindings', r'\bint32_t\s+___0_\w+')
    style_path, style_source, style_match, style_end = method(cpp, 'Assembly-CSharp', 'NewUnityInputDevice_DetectDeviceStyle')
    game_path, game_source, game_match, game_end = method(cpp, 'Assembly-CSharp', 'InputHandler_SetActiveGamepadType')
    style_body = style_source[style_match.start():style_end]
    game_body = game_source[game_match.start():game_end]
    style_parameter = re.search(r'\b(InputDevice_t\w*)\*\s+(___0_\w+)', style_match[0])
    game_parameter = re.search(r'\b(InputDevice_t\w*)\*\s+(___0_\w+)', game_match[0])
    assert style_parameter and style_parameter[1] == style_arg[1], 'Style device ABI changed'
    assert game_parameter and game_parameter[1] == resolve_arg[1], 'Game device ABI changed'
    assert all(name not in style_source and name not in game_source for name in (style, resolve, prepare)), 'Identity hook already applied'
    assert 'SwitchProControllerHID' in style_body and 'DualSenseGamepadHID' in style_body, 'Original desktop detector changed'
    assert 'InputDevice_get_DeviceStyle_' in game_body and 'String_ToLowerInvariant_' in game_body
    map_call = game_body.index('InputHandler_MapControllerButtons_')
    labels = list(re.finditer(r'\n(IL_[0-9a-fA-F]+):\n\t\{', game_body[:map_call]))
    assert labels, 'Original mapping continuation not found'
    mapping_label = labels[-1][1]
    block = game_body[labels[-1].end():map_call]
    assert re.fullmatch(r'\s*int32_t L_\d+ = __this->___activeGamepadType;\s*', block), 'Mapping continuation changed'
    assert game_body.count('InputHandler_MapControllerButtons_') == 1
    platform = re.search(r'\n\t\{\n\t\til2cpp_codegen_runtime_class_init_inline\(Application_t\w+_il2cpp_TypeInfo_var\);\n\t\tint32_t L_\d+;\n\t\tL_\d+ = Application_get_platform_', game_body)
    assert platform and '___gamepadState' in game_body[:platform.start()] and 'InputHandler_set_ActiveGamepadAlias_' in game_body[:platform.start()], 'Original detached guard changed'
    opening = style_body.index('\n{') + 2
    style_prefix = (f'\n\tint32_t nativeStyle = {style}({style_parameter[2]}, NULL);\n'
                    '\tif (nativeStyle >= 0) return nativeStyle;\n')
    style_replacement = style_decl + '\n' + style_body[:opening] + style_prefix + style_body[opening:]
    game_prefix = ('\n\t{\n'
                   f'\t\tint32_t nativeType = {resolve}({game_parameter[2]}, NULL);\n'
                   '\t\tif (nativeType >= 0)\n\t\t{\n'
                   f'\t\t\t{prepare}(nativeType, NULL);\n'
                   '\t\t\t__this->___activeGamepadType = nativeType;\n'
                   f'\t\t\tgoto {mapping_label};\n\t\t}}\n\t}}\n')
    game_replacement = resolve_decl + '\n' + prepare_decl + '\n' + game_body[:platform.start()] + game_prefix + game_body[platform.start():]
    # Validate both boundaries before writing either generated file.
    changes = [(style_path, style_match.start(), style_end, style_replacement),
               (game_path, game_match.start(), game_end, game_replacement)]
    for path, source in {style_path: style_source, game_path: game_source}.items():
        for _, start, end, replacement in sorted((c for c in changes if c[0] == path), key=lambda c: c[1], reverse=True):
            source = source[:start] + replacement + source[end:]
        path.write_text(source, encoding='utf-8', newline='\n')
    return dict(style_file=style_path.name, game_file=game_path.name,
                boundary='iOS identity before command controls and original binding setup',
                original_constructor_actions_and_binding_methods_retained=True,
                original_desktop_detector_and_name_fallback_retained=True,
                ios_name_fallback_bypassed=True, face_button_positions_unchanged=True,
                missing_ps5_settings_copy_legacy_ps4=True, runtime_verified=False)
