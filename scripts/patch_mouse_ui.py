"""Connect original menu pointer dispatch to the iOS mouse input boundary."""
from patch_original_display import METHOD
import re


def apply(cpp):
    helper = (cpp / 'NativeCompatibility.cpp').read_text(encoding='utf-8')
    helpers = {}
    for match in METHOD.finditer(helper):
        name = match[1].rsplit('_m', 1)[0]
        if name.startswith(('NativeMouseUI_', 'NativeBindingLabel_')):
            helpers[name] = (match[1], match[0].rstrip()[:-1].rstrip() + ';')
    required = {'HollowKnightInputModule_Process', 'HollowKnightInputModule_UpdateModule',
                'HollowKnightInputModule_ActivateModule', 'HollowKnightInputModule_get_MouseButtonIsPressed',
                'StandaloneInputModule_ProcessMousePress', 'MappableKey_OnBindingAdded',
                'InputHandler_Update', 'MappableKey_ListenForNewButton',
                'MappableKey_ShowCurrentBinding', 'MappableKey_AbortRebind', 'MappableKey_OnDisable'}
    counts = {name: 0 for name in required}
    for path in sorted([*cpp.glob('Assembly-CSharp*.cpp'), *cpp.glob('UnityEngine.UI*.cpp')]):
        source = path.read_text(encoding='utf-8')
        edits, used = [], set()
        for match in METHOD.finditer(source):
            name = match[1].rsplit('_m', 1)[0]
            if name not in required: continue
            end = source.index('\n}', match.end()) + 2
            body = source[match.start():end]
            prefix, suffix = '', ''
            def call(key, arguments):
                used.add(key)
                return helpers[key][0] + '(' + arguments + ', NULL)'
            if name in ('HollowKnightInputModule_Process', 'HollowKnightInputModule_UpdateModule'):
                prefix = call('NativeMouseUI_Prepare', '__this') + ';'
            elif name == 'HollowKnightInputModule_get_MouseButtonIsPressed':
                body, n = re.subn(r'\bInput_GetMouseButtonDown_m[0-9A-F]+(?=\()', helpers['NativeMouseUI_MouseButtonDown'][0], body)
                assert n == 1, (name, n)
                used.add('NativeMouseUI_MouseButtonDown')
            elif name == 'StandaloneInputModule_ProcessMousePress':
                prefix = 'if (' + call('NativeMouseUI_ConsumeMouse', '__this, ___0_data') + ') return;'
            elif name == 'MappableKey_OnBindingAdded':
                suffix = call('NativeMouseUI_BindingAdded', '___0_action, ___1_binding') + ';'
            elif name == 'InputHandler_Update':
                body, n = re.subn(r'\bInput_GetAxisRaw_m[0-9A-F]+(?=\()', helpers['NativeMouseUI_MouseWakeAxis'][0], body)
                assert n == 1, (name, n)
                used.add('NativeMouseUI_MouseWakeAxis')
            elif name == 'MappableKey_ListenForNewButton':
                suffix = call('NativeBindingLabel_ShowPrompt', '__this') + ';'
            elif name != 'HollowKnightInputModule_ActivateModule':
                prefix = call('NativeBindingLabel_Restore', '__this') + ';'
            if name in ('HollowKnightInputModule_UpdateModule', 'HollowKnightInputModule_ActivateModule'):
                body, n = re.subn(r'\bInput_get_mousePosition_m[0-9A-F]+(?=\()', helpers['NativeMouseUI_PointerPosition'][0], body)
                assert n == (1 if name.endswith('_UpdateModule') else 2), (name, n)
                used.add('NativeMouseUI_PointerPosition')
            if prefix:
                opening = body.index('\n{') + 2
                body = body[:opening] + '\n\t' + prefix + body[opening:]
            if suffix:
                closing = body.rfind('\treturn;')
                assert closing > 0
                body = body[:closing] + '\t' + suffix + '\n' + body[closing:]
            edits.append((match.start(), end, body))
            counts[name] += 1
        if edits:
            for start, end, body in reversed(edits): source = source[:start] + body + source[end:]
            insertion = METHOD.search(source).start()
            source = source[:insertion] + '\n// iOS mouse/UI compatibility boundary.\n' + '\n'.join(helpers[key][1] for key in sorted(used)) + '\n' + source[insertion:]
            path.write_text(source, encoding='utf-8', newline='\n')
    assert all(n == 1 for n in counts.values()), counts
    return dict(hooks=counts, menu_module='HollowKnightInputModule',
                pointer_dispatch='StandaloneInputModule.ProcessMousePress',
                original_bindings_and_menu_retained=True, runtime_verified=False)
